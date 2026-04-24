using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AvatarVariantOscBridge;

internal sealed record DiscoveredService(
    string InstanceName,
    string ServiceType,
    IPAddress Address,
    int Port);

internal sealed class MDnsServiceAdvertisement
{
    // Fully qualified instance name, e.g. "AvatarVariantSwitcher-1234._osc._udp.local."
    public string InstanceFqdn { get; init; } = string.Empty;
    // Service type, e.g. "_osc._udp.local."
    public string ServiceType { get; init; } = string.Empty;
    // Hostname the SRV record points to, e.g. "avs-1234.local."
    public string HostFqdn { get; init; } = string.Empty;
    public ushort Port { get; init; }
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    public List<string> TxtItems { get; init; } = new() { "txtvers=1" };
}

/// <summary>
/// Minimal mDNS responder / browser on 224.0.0.251:5353. Supports:
/// - Binding with SO_REUSEADDR so multiple processes can share the port.
/// - Advertising a set of services (unsolicited announcement + answering queries).
/// - Browsing: sending PTR queries and surfacing discovered services.
///
/// We intentionally skip probing / conflict resolution (RFC 6762 §8) — for our
/// use case the service name already includes a random suffix, and a second
/// bridge running at the same time is OK if they pick different names.
/// </summary>
internal sealed class MDnsResponder : IDisposable
{
    private static readonly IPEndPoint MulticastEndpoint =
        new(IPAddress.Parse("224.0.0.251"), 5353);

    public event Action<DiscoveredService>? ServiceDiscovered;
    public event Action<string>? LogInfo;
    public event Action<string>? LogWarn;
    public event Action<string>? LogDebug;

    private readonly List<MDnsServiceAdvertisement> _advertisements = new();
    private readonly HashSet<string> _browseTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DiscoveryState> _discoveryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _hostCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly object _sendLock = new();

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _maintenanceTask;
    private bool _started;
    // IPv4 addresses of every interface we joined multicast on; also used as the
    // per-packet outgoing interface list so announcements/queries fan out to all
    // physical and virtual NICs instead of just Windows' default-route NIC.
    private IReadOnlyList<IPAddress> _multicastInterfaces = Array.Empty<IPAddress>();

    public void RegisterService(MDnsServiceAdvertisement ad)
    {
        if (_started) throw new InvalidOperationException("Register services before Start().");
        _advertisements.Add(ad);
    }

    public void BrowseFor(string serviceType)
    {
        if (_started) throw new InvalidOperationException("Register browse types before Start().");
        _browseTypes.Add(serviceType);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        _socket = socket;

        // AddMembership(IPAddress.Any) on Windows only joins one NIC (picked by the
        // routing table). On a multi-homed machine — Hyper-V, WSL, Meta Quest Link,
        // VirtualBox, VPNs all create extra NICs — that's often a 169.254.x.x APIPA
        // or virtual adapter, and we miss VRChat's announcements on the real LAN.
        // Join every Up IPv4 NIC explicitly and remember them for per-send fanout.
        _multicastInterfaces = JoinMulticastOnAllInterfaces(socket);
        if (_multicastInterfaces.Count == 0)
            LogWarn?.Invoke("mDNS: no multicast-capable IPv4 interfaces found; discovery will not work.");
        else
            LogInfo?.Invoke($"mDNS joined multicast on {_multicastInterfaces.Count} interface(s): " +
                string.Join(", ", _multicastInterfaces));

        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(_cts.Token));

        // Initial announce + browse queries.
        AnnounceSelf();
        SendBrowseQueries();
    }

    private List<IPAddress> JoinMulticastOnAllInterfaces(Socket socket)
    {
        var joined = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!ni.SupportsMulticast) continue;

            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch { continue; }

            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(MulticastEndpoint.Address, uni.Address));
                    joined.Add(uni.Address);
                }
                catch (SocketException ex)
                {
                    LogWarn?.Invoke($"mDNS AddMembership on {ni.Name} ({uni.Address}) failed: {ex.Message}");
                }
                break; // one IPv4 address per NIC is enough
            }
        }
        return joined;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[9000];
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _socket!.ReceiveFromAsync(
                    buffer, from, ct).ConfigureAwait(false);
                var length = result.ReceivedBytes;
                var packetBytes = new byte[length];
                Buffer.BlockCopy(buffer, 0, packetBytes, 0, length);

                if (!DnsPacket.TryParse(packetBytes, out var packet) || packet == null)
                {
                    LogDebug?.Invoke($"rx {length}B from {result.RemoteEndPoint}: parse failed");
                    continue;
                }

                if (LogDebug != null)
                    LogDebug.Invoke($"rx from {result.RemoteEndPoint}: {SummarizePacket(packet)}");

                HandlePacket(packet);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                LogWarn?.Invoke($"mDNS receive error: {ex.Message}");
            }
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        // Re-announce + re-query every 30s to keep caches warm and to catch VRChat
        // starting up later than the bridge.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                AnnounceSelf();
                SendBrowseQueries();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void HandlePacket(DnsPacket packet)
    {
        // Answer incoming questions for services we advertise.
        if (!packet.IsResponse)
        {
            foreach (var q in packet.Questions)
                AnswerIfOurs(q);
        }

        // Harvest records (answers + additionals) to resolve browsed services.
        var allRecords = new List<DnsRecord>(packet.Answers.Count + packet.Additionals.Count + packet.Authorities.Count);
        allRecords.AddRange(packet.Answers);
        allRecords.AddRange(packet.Authorities);
        allRecords.AddRange(packet.Additionals);
        if (allRecords.Count == 0) return;

        lock (_stateLock)
        {
            // Pass 1: cache A records so SRV resolution has targets.
            var refreshedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in allRecords)
            {
                if (r.Type == DnsCodec.TypeA && r.Address != null)
                {
                    _hostCache[r.Name] = r.Address;
                    refreshedHosts.Add(r.Name);
                }
            }

            foreach (var state in _discoveryCache.Values)
            {
                if (!string.IsNullOrEmpty(state.Target) && refreshedHosts.Contains(state.Target))
                    TryFlushDiscovered(state);
            }

            // Pass 2: PTR / SRV — populate discovery state.
            foreach (var r in allRecords)
            {
                if (r.Type == DnsCodec.TypePtr && r.PtrName != null && IsBrowsedType(r.Name))
                {
                    var state = GetOrCreate(r.PtrName, r.Name);
                    // PTR alone is not enough — keep waiting for SRV.
                    TryFlushDiscovered(state);
                }
                else if (r.Type == DnsCodec.TypeSrv && r.Srv != null)
                {
                    // SRV's owner is an instance name like VRChat-Client-XXX._osc._udp.local.
                    var serviceType = ExtractServiceType(r.Name);
                    if (string.IsNullOrEmpty(serviceType) || !IsBrowsedType(serviceType)) continue;

                    var state = GetOrCreate(r.Name, serviceType);
                    state.Port = r.Srv.Port;
                    state.Target = r.Srv.Target;
                    TryFlushDiscovered(state);
                }
            }
        }
    }

    private DiscoveryState GetOrCreate(string instanceName, string serviceType)
    {
        if (!_discoveryCache.TryGetValue(instanceName, out var state))
        {
            state = new DiscoveryState { InstanceName = instanceName, ServiceType = serviceType };
            _discoveryCache[instanceName] = state;
        }
        return state;
    }

    private void TryFlushDiscovered(DiscoveryState state)
    {
        if (state.Fired) return;
        if (state.Port == null || string.IsNullOrEmpty(state.Target)) return;

        IPAddress? addr = null;
        if (_hostCache.TryGetValue(state.Target!, out var cached))
            addr = cached;
        else if (state.Target == "localhost." || state.Target == "localhost")
            addr = IPAddress.Loopback;

        if (addr == null)
        {
            // Ask the network for an A record matching this target.
            SendQuery(state.Target!, DnsCodec.TypeA);
            return;
        }

        state.Fired = true;
        var svc = new DiscoveredService(state.InstanceName, state.ServiceType, addr, state.Port!.Value);
        try { ServiceDiscovered?.Invoke(svc); }
        catch (Exception ex) { LogWarn?.Invoke($"ServiceDiscovered handler threw: {ex.Message}"); }
    }

    private void AnswerIfOurs(DnsQuestion q)
    {
        if (_advertisements.Count == 0) return;
        foreach (var ad in _advertisements)
        {
            var wantsPtr = q.Type is DnsCodec.TypePtr or DnsCodec.TypeAny;
            var wantsSrv = q.Type is DnsCodec.TypeSrv or DnsCodec.TypeAny;
            var wantsTxt = q.Type is DnsCodec.TypeTxt or DnsCodec.TypeAny;
            var wantsA   = q.Type is DnsCodec.TypeA or DnsCodec.TypeAny;

            if (string.Equals(q.Name, ad.ServiceType, StringComparison.OrdinalIgnoreCase) && wantsPtr)
            {
                SendAnnouncement(ad);
            }
            else if (string.Equals(q.Name, ad.InstanceFqdn, StringComparison.OrdinalIgnoreCase) && (wantsSrv || wantsTxt))
            {
                SendAnnouncement(ad);
            }
            else if (string.Equals(q.Name, ad.HostFqdn, StringComparison.OrdinalIgnoreCase) && wantsA)
            {
                SendAnnouncement(ad);
            }
        }
    }

    public void AnnounceSelf()
    {
        foreach (var ad in _advertisements)
            SendAnnouncement(ad);
    }

    private void SendAnnouncement(MDnsServiceAdvertisement ad)
    {
        var pkt = new DnsPacket
        {
            TransactionId = 0,
            IsResponse = true,
            AuthoritativeAnswer = true,
        };
        pkt.Answers.Add(new DnsRecord
        {
            Name = ad.ServiceType,
            Type = DnsCodec.TypePtr,
            Class = DnsCodec.ClassIn,
            Ttl = 4500,
            PtrName = ad.InstanceFqdn,
        });
        pkt.Additionals.Add(new DnsRecord
        {
            Name = ad.InstanceFqdn,
            Type = DnsCodec.TypeSrv,
            Class = DnsCodec.ClassInCacheFlush,
            Ttl = 120,
            Srv = new DnsSrvData
            {
                Priority = 0, Weight = 0, Port = ad.Port, Target = ad.HostFqdn,
            },
        });
        pkt.Additionals.Add(new DnsRecord
        {
            Name = ad.InstanceFqdn,
            Type = DnsCodec.TypeTxt,
            Class = DnsCodec.ClassInCacheFlush,
            Ttl = 4500,
            TxtItems = ad.TxtItems.Count > 0 ? new List<string>(ad.TxtItems) : new List<string> { "txtvers=1" },
        });
        pkt.Additionals.Add(new DnsRecord
        {
            Name = ad.HostFqdn,
            Type = DnsCodec.TypeA,
            Class = DnsCodec.ClassInCacheFlush,
            Ttl = 120,
            Address = ad.Address,
        });

        SendPacket(pkt);
    }

    private void SendBrowseQueries()
    {
        foreach (var type in _browseTypes)
            SendQuery(type, DnsCodec.TypePtr);
    }

    private void SendQuery(string name, ushort type)
    {
        var pkt = new DnsPacket
        {
            TransactionId = 0,
            IsResponse = false,
        };
        pkt.Questions.Add(new DnsQuestion { Name = name, Type = type, Class = DnsCodec.ClassIn });
        SendPacket(pkt);
    }

    private void SendPacket(DnsPacket pkt)
    {
        var socket = _socket;
        if (socket == null) return;

        byte[] bytes;
        try { bytes = pkt.Serialize(); }
        catch (Exception ex)
        {
            LogWarn?.Invoke($"mDNS serialize error: {ex.Message}");
            return;
        }

        var interfaces = _multicastInterfaces;
        if (interfaces.Count == 0)
        {
            try { socket.SendTo(bytes, SocketFlags.None, MulticastEndpoint); }
            catch (Exception ex) { LogWarn?.Invoke($"mDNS send error: {ex.Message}"); }
            LogDebug?.Invoke($"tx via OS-default: {SummarizePacket(pkt)}");
            return;
        }

        lock (_sendLock)
        {
            foreach (var ifaddr in interfaces)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        ifaddr.GetAddressBytes());
                    socket.SendTo(bytes, SocketFlags.None, MulticastEndpoint);
                    LogDebug?.Invoke($"tx via {ifaddr}: {SummarizePacket(pkt)}");
                }
                catch (Exception ex)
                {
                    LogWarn?.Invoke($"mDNS send via {ifaddr} failed: {ex.Message}");
                }
            }
        }
    }

    public void SendGoodbye()
    {
        // TTL=0 signals "remove immediately" (RFC 6762 §10.1). Best-effort.
        foreach (var ad in _advertisements)
        {
            var pkt = new DnsPacket
            {
                TransactionId = 0,
                IsResponse = true,
                AuthoritativeAnswer = true,
            };
            pkt.Answers.Add(new DnsRecord
            {
                Name = ad.ServiceType,
                Type = DnsCodec.TypePtr,
                Class = DnsCodec.ClassIn,
                Ttl = 0,
                PtrName = ad.InstanceFqdn,
            });
            SendPacket(pkt);
        }
    }

    private bool IsBrowsedType(string name) => _browseTypes.Contains(name);

    // Extract "foo._osc._udp.local." service type from an instance name like
    // "VRChat-Client-ABC._osc._udp.local." → returns "_osc._udp.local.".
    private static string ExtractServiceType(string instanceName)
    {
        var dot = instanceName.IndexOf('.');
        return dot < 0 ? string.Empty : instanceName.Substring(dot + 1);
    }

    private static string SummarizePacket(DnsPacket pkt)
    {
        var kind = pkt.IsResponse ? "resp" : "query";
        var sb = new System.Text.StringBuilder();
        sb.Append(kind);
        sb.Append(' ');
        sb.Append($"Q={pkt.Questions.Count} A={pkt.Answers.Count} N={pkt.Authorities.Count} Ad={pkt.Additionals.Count}");
        AppendHighlights(sb, "Q", pkt.Questions.Select(q => $"{TypeName(q.Type)}:{q.Name}"));
        AppendHighlights(sb, "A", pkt.Answers.Select(SummarizeRecord));
        AppendHighlights(sb, "Ad", pkt.Additionals.Select(SummarizeRecord));
        return sb.ToString();
    }

    private static void AppendHighlights(System.Text.StringBuilder sb, string label, IEnumerable<string> items)
    {
        var list = items.Where(s => !string.IsNullOrEmpty(s)).Take(4).ToList();
        if (list.Count == 0) return;
        sb.Append(' ');
        sb.Append(label);
        sb.Append("=[");
        sb.Append(string.Join("; ", list));
        sb.Append(']');
    }

    private static string SummarizeRecord(DnsRecord r) => r.Type switch
    {
        DnsCodec.TypePtr => $"PTR {r.Name} -> {r.PtrName}",
        DnsCodec.TypeSrv => $"SRV {r.Name} -> {r.Srv?.Target}:{r.Srv?.Port}",
        DnsCodec.TypeA => $"A {r.Name} -> {r.Address}",
        DnsCodec.TypeTxt => $"TXT {r.Name}",
        _ => $"type{r.Type} {r.Name}",
    };

    private static string TypeName(ushort t) => t switch
    {
        DnsCodec.TypePtr => "PTR",
        DnsCodec.TypeSrv => "SRV",
        DnsCodec.TypeA => "A",
        DnsCodec.TypeTxt => "TXT",
        DnsCodec.TypeAny => "ANY",
        _ => $"type{t}",
    };

    public void Dispose()
    {
        try { SendGoodbye(); } catch { /* best effort */ }
        _cts?.Cancel();
        try { _socket?.Close(); } catch { }
        try { _receiveTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { _maintenanceTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts?.Dispose();
    }

    private sealed class DiscoveryState
    {
        public string InstanceName = string.Empty;
        public string ServiceType = string.Empty;
        public ushort? Port;
        public string? Target;
        public bool Fired;
    }
}

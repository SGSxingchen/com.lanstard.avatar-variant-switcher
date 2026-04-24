using System.Net;
using System.Net.Sockets;

namespace AvatarVariantOscBridge;

/// <summary>
/// Advertises our OSC receiver via OSCQuery (mDNS + HTTP schema) and browses
/// for VRChat's OSC endpoint so we can send /avatar/change to it. When VRChat
/// isn't advertising, BroadcastTarget is null and /avatar/change sends are
/// skipped with a log line.
/// </summary>
internal sealed class OscQueryService : IDisposable
{
    private const string OscUdpServiceType = "_osc._udp.local.";
    private const string OscQueryTcpServiceType = "_oscjson._tcp.local.";
    private const string VrchatClientPrefix = "VRChat-Client-";

    public int LocalOscPort { get; }
    public int LocalHttpPort { get; }
    public string ServiceName { get; }

    // The endpoint VRChat-Client-* last advertised on _osc._udp.
    // Null until we discover one.
    public IPEndPoint? BroadcastTarget
    {
        get
        {
            lock (_stateLock) return _broadcastTarget;
        }
    }

    public event Action<IPEndPoint>? VrchatDiscovered;
    public event Action<string>? LogInfo;
    public event Action<string>? LogWarn;
    public event Action<string>? LogDebug;

    private readonly string _oscInstanceFqdn;
    private readonly string _oscQueryInstanceFqdn;
    private readonly string _hostFqdn;
    private readonly object _stateLock = new();

    private string _currentParameterName;
    private OscQueryNode _tree;
    private OscQueryHostInfo _hostInfo;
    private IPEndPoint? _broadcastTarget;

    private readonly MDnsResponder _mdns = new();
    private OscQueryHttpHost? _http;

    public OscQueryService(string baseName, string parameterName, int oscUdpPort)
    {
        LocalOscPort = oscUdpPort;
        LocalHttpPort = FindFreeTcpPort();
        var suffix = Environment.ProcessId.ToString("x8");
        ServiceName = $"{baseName}-{suffix}";
        _hostFqdn = $"{SanitizeLabel(ServiceName)}.local.";
        _oscInstanceFqdn = $"{ServiceName}.{OscUdpServiceType}";
        _oscQueryInstanceFqdn = $"{ServiceName}.{OscQueryTcpServiceType}";

        _currentParameterName = parameterName;
        _tree = BuildTree(parameterName);
        _hostInfo = BuildHostInfo(ServiceName, oscUdpPort);
    }

    public void Start()
    {
        _http = new OscQueryHttpHost(LocalHttpPort, SnapshotTree, SnapshotHostInfo);
        _http.LogWarn += s => LogWarn?.Invoke(s);
        _http.Start();

        _mdns.LogInfo += s => LogInfo?.Invoke(s);
        _mdns.LogWarn += s => LogWarn?.Invoke(s);
        _mdns.LogDebug += s => LogDebug?.Invoke(s);

        _mdns.RegisterService(new MDnsServiceAdvertisement
        {
            InstanceFqdn = _oscInstanceFqdn,
            ServiceType = OscUdpServiceType,
            HostFqdn = _hostFqdn,
            Port = (ushort)LocalOscPort,
            Address = IPAddress.Loopback,
            TxtItems = new List<string> { "txtvers=1" },
        });
        _mdns.RegisterService(new MDnsServiceAdvertisement
        {
            InstanceFqdn = _oscQueryInstanceFqdn,
            ServiceType = OscQueryTcpServiceType,
            HostFqdn = _hostFqdn,
            Port = (ushort)LocalHttpPort,
            Address = IPAddress.Loopback,
            TxtItems = new List<string> { "txtvers=1" },
        });

        _mdns.BrowseFor(OscUdpServiceType);
        _mdns.BrowseFor(OscQueryTcpServiceType);
        _mdns.ServiceDiscovered += OnServiceDiscovered;

        _mdns.Start();
    }

    public void UpdateParameterName(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        lock (_stateLock)
        {
            _currentParameterName = newName;
            _tree = BuildTree(newName);
        }
        LogInfo?.Invoke($"OSCQuery tree updated: /avatar/parameters/{newName}");
    }

    private void OnServiceDiscovered(DiscoveredService svc)
    {
        if (!string.Equals(svc.ServiceType, OscUdpServiceType, StringComparison.OrdinalIgnoreCase))
            return;

        // Require VRChat-Client-* service name; ignore our own advertisement and third parties.
        var instance = svc.InstanceName;
        var dot = instance.IndexOf('.');
        var bareName = dot < 0 ? instance : instance.Substring(0, dot);
        if (!bareName.StartsWith(VrchatClientPrefix, StringComparison.Ordinal))
            return;

        var target = new IPEndPoint(svc.Address, svc.Port);
        lock (_stateLock)
        {
            if (_broadcastTarget != null &&
                _broadcastTarget.Address.Equals(target.Address) &&
                _broadcastTarget.Port == target.Port)
                return;
            _broadcastTarget = target;
        }
        LogInfo?.Invoke($"VRChat discovered at {target} (service {bareName})");
        try { VrchatDiscovered?.Invoke(target); }
        catch (Exception ex) { LogWarn?.Invoke($"VrchatDiscovered handler threw: {ex.Message}"); }
    }

    private OscQueryNode SnapshotTree()
    {
        lock (_stateLock) return _tree;
    }

    private OscQueryHostInfo SnapshotHostInfo()
    {
        lock (_stateLock) return _hostInfo;
    }

    private static OscQueryNode BuildTree(string parameterName)
    {
        var parameterNode = new OscQueryNode
        {
            FullPath = $"/avatar/parameters/{parameterName}",
            Access = OscQueryAccess.WriteOnly,
            Type = "i",
            Description = "Avatar variant index",
        };
        var changeNode = new OscQueryNode
        {
            FullPath = "/avatar/change",
            Access = OscQueryAccess.WriteOnly,
            Type = "s",
            Description = "Current avatar blueprint id (echoed by VRChat)",
        };
        var parametersNode = new OscQueryNode
        {
            FullPath = "/avatar/parameters",
            Access = OscQueryAccess.NoAccess,
            Contents = new Dictionary<string, OscQueryNode>
            {
                [parameterName] = parameterNode,
            },
        };
        var avatarNode = new OscQueryNode
        {
            FullPath = "/avatar",
            Access = OscQueryAccess.NoAccess,
            Contents = new Dictionary<string, OscQueryNode>
            {
                ["parameters"] = parametersNode,
                ["change"] = changeNode,
            },
        };
        return new OscQueryNode
        {
            FullPath = "/",
            Access = OscQueryAccess.NoAccess,
            Contents = new Dictionary<string, OscQueryNode>
            {
                ["avatar"] = avatarNode,
            },
        };
    }

    private static OscQueryHostInfo BuildHostInfo(string name, int oscPort) =>
        new()
        {
            Name = name,
            OscIp = "127.0.0.1",
            OscPort = oscPort,
            OscTransport = "UDP",
        };

    private static int FindFreeTcpPort()
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)sock.LocalEndPoint!).Port;
    }

    private static string SanitizeLabel(string input)
    {
        Span<char> buf = stackalloc char[input.Length];
        var i = 0;
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-') buf[i++] = ch;
            else buf[i++] = '-';
        }
        return new string(buf[..i]);
    }

    public void Dispose()
    {
        try { _mdns.Dispose(); } catch { }
        try { _http?.Dispose(); } catch { }
    }
}

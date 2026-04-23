using System.Net;
using System.Net.Sockets;

namespace AvatarVariantOscBridge;

internal sealed class VariantBridge
{
    private const string ServiceBaseName = "AvatarVariantSwitcher";

    private readonly BridgeOptions _options;
    private readonly UdpClient _listener;
    private readonly UdpClient _sender;
    private readonly IPEndPoint? _legacyTargetEndpoint;
    private readonly int _listenPort;
    private readonly OscQueryService? _oscQuery;

    private readonly object _mapLock = new();
    private AvatarVariantMap _map;
    private string _parameterAddress;
    private Dictionary<int, AvatarVariantMapEntry> _byParamValue;

    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private const int ReloadDebounceMs = 500;

    private string? _currentAvatarId;
    private int? _lastObservedValue;

    public VariantBridge(BridgeOptions options)
    {
        _options = options;
        _map = AvatarVariantMap.Load(options.MappingPath);
        _parameterAddress = BuildParameterAddress(_map.ParameterName);
        _byParamValue = BuildIndex(_map);

        if (options.Legacy)
        {
            _listener = new UdpClient(new IPEndPoint(IPAddress.Any, options.ListenPort));
            _listenPort = options.ListenPort;
            _legacyTargetEndpoint = new IPEndPoint(IPAddress.Parse(options.Host), options.SendPort);
        }
        else
        {
            _listener = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _listenPort = ((IPEndPoint)_listener.Client.LocalEndPoint!).Port;
            _legacyTargetEndpoint = null;

            _oscQuery = new OscQueryService(ServiceBaseName, _map.ParameterName, _listenPort);
            _oscQuery.LogInfo += msg => Console.WriteLine($"[{Timestamp()}] {msg}");
            _oscQuery.LogWarn += msg => Console.Error.WriteLine($"[{Timestamp()}] WARN: {msg}");
            _oscQuery.VrchatDiscovered += ep => Console.WriteLine($"[{Timestamp()}] sending /avatar/change to {ep}");
            _oscQuery.Start();
        }

        _sender = new UdpClient();

        TryCreateWatcher();
    }

    public async Task RunAsync()
    {
        PrintStartupInfo();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await _listener.ReceiveAsync(cts.Token);
                if (!OscCodec.TryReadMessage(result.Buffer, out var message) || message == null)
                    continue;
                HandleMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            // clean shutdown
        }
        finally
        {
            _reloadTimer?.Dispose();
            _watcher?.Dispose();
            _listener.Dispose();
            _sender.Dispose();
            _oscQuery?.Dispose();
        }
    }

    private void HandleMessage(OscMessage message)
    {
        // Track avatar id reported by VRChat for redundant-switch suppression.
        if (string.Equals(message.Address, "/avatar/change", StringComparison.Ordinal))
        {
            if (message.Arguments.Count > 0 && message.Arguments[0] is string avatarId)
            {
                _currentAvatarId = avatarId;
                Console.WriteLine($"[{Timestamp()}] current avatar -> {avatarId}");
            }
            return;
        }

        string expectedAddress;
        Dictionary<int, AvatarVariantMapEntry> index;
        lock (_mapLock)
        {
            expectedAddress = _parameterAddress;
            index = _byParamValue;
        }

        if (!string.Equals(message.Address, expectedAddress, StringComparison.Ordinal))
            return;
        if (!TryReadIntArg(message, out var value))
            return;
        if (_lastObservedValue == value)
            return;

        _lastObservedValue = value;

        if (!index.TryGetValue(value, out var entry))
        {
            Console.WriteLine($"[{Timestamp()}] value={value}: no variant mapped.");
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.BlueprintId))
        {
            Console.WriteLine($"[{Timestamp()}] value={value} ({entry.DisplayName}): blueprintId is empty — not uploaded yet?");
            return;
        }
        if (string.Equals(entry.BlueprintId, _currentAvatarId, StringComparison.Ordinal))
        {
            Console.WriteLine($"[{Timestamp()}] value={value}: already on {entry.BlueprintId}.");
            return;
        }

        var target = ResolveSendTarget();
        if (target == null)
        {
            Console.WriteLine($"[{Timestamp()}] value={value} ({entry.DisplayName}): VRChat not yet discovered via OSCQuery, dropping switch.");
            return;
        }

        var packet = OscCodec.WriteMessage("/avatar/change", entry.BlueprintId);
        _sender.Send(packet, packet.Length, target);
        _currentAvatarId = entry.BlueprintId;
        Console.WriteLine($"[{Timestamp()}] value={value} -> {entry.DisplayName} ({entry.BlueprintId})");
    }

    private IPEndPoint? ResolveSendTarget()
    {
        if (_options.Legacy) return _legacyTargetEndpoint;
        return _oscQuery?.BroadcastTarget;
    }

    private static bool TryReadIntArg(OscMessage message, out int value)
    {
        value = default;
        if (message.Arguments.Count == 0) return false;
        switch (message.Arguments[0])
        {
            case int i: value = i; return true;
            case float f: value = (int)MathF.Round(f); return true;
            case bool b: value = b ? 1 : 0; return true;
            default: return false;
        }
    }

    private void TryCreateWatcher()
    {
        var directory = Path.GetDirectoryName(_options.MappingPath);
        var fileName = Path.GetFileName(_options.MappingPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
            return;

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
        };
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
        _watcher.EnableRaisingEvents = true;
    }

    private void ScheduleReload()
    {
        // Debounce bursts of filesystem events (editor writes .tmp then File.Move).
        _reloadTimer?.Dispose();
        _reloadTimer = new Timer(_ => Reload(), null, ReloadDebounceMs, Timeout.Infinite);
    }

    private void Reload()
    {
        try
        {
            var fresh = AvatarVariantMap.Load(_options.MappingPath);
            string oldName;
            string newName;
            lock (_mapLock)
            {
                oldName = _map.ParameterName;
                _map = fresh;
                _parameterAddress = BuildParameterAddress(_map.ParameterName);
                _byParamValue = BuildIndex(_map);
                newName = _map.ParameterName;
            }
            _oscQuery?.UpdateParameterName(oldName, newName);
            Console.WriteLine($"[{Timestamp()}] mapping reloaded ({_map.Variants.Count} variants, parameter={_map.ParameterName}).");
        }
        catch (Exception ex)
        {
            // Keep last-good map on parse failure (e.g. half-written file).
            Console.Error.WriteLine($"[{Timestamp()}] WARN: mapping reload failed, keeping previous map: {ex.Message}");
        }
    }

    private void PrintStartupInfo()
    {
        Console.WriteLine($"Mapping:    {_options.MappingPath}");
        if (_options.Legacy)
        {
            Console.WriteLine($"Mode:       legacy (static ports)");
            Console.WriteLine($"Listening:  0.0.0.0:{_listenPort}");
            Console.WriteLine($"Sending to: {_legacyTargetEndpoint}");
        }
        else
        {
            Console.WriteLine($"Mode:       OSCQuery (mDNS auto-discovery)");
            Console.WriteLine($"Listening:  0.0.0.0:{_listenPort} (OSC UDP, dynamic)");
            Console.WriteLine($"Schema URL: http://127.0.0.1:{_oscQuery!.LocalHttpPort}/");
            Console.WriteLine($"Sending to: (awaiting VRChat broadcast via mDNS)");
        }
        Console.WriteLine($"Parameter:  {_map.ParameterName}  ({_map.Variants.Count} variants)");
        foreach (var v in _map.Variants)
        {
            var id = string.IsNullOrWhiteSpace(v.BlueprintId) ? "(not uploaded)" : v.BlueprintId;
            Console.WriteLine($"  {v.ParamValue,3} | {v.DisplayName,-24} | {id}");
        }
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();
    }

    private static Dictionary<int, AvatarVariantMapEntry> BuildIndex(AvatarVariantMap map)
    {
        var d = new Dictionary<int, AvatarVariantMapEntry>();
        foreach (var v in map.Variants)
        {
            if (v == null) continue;
            d[v.ParamValue] = v;
        }
        return d;
    }

    private static string BuildParameterAddress(string parameterName)
        => $"/avatar/parameters/{parameterName}";

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss");
}

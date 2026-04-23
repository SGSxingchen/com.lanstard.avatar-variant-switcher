using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var options = BridgeOptions.Parse(args);
if (options == null)
{
    return 1;
}

var bridge = new AvatarVariantOscBridge(options);
await bridge.RunAsync();
return 0;

internal sealed class AvatarVariantOscBridge
{
    private readonly BridgeOptions _options;
    private readonly UdpClient _listener;
    private readonly UdpClient _sender;
    private readonly IPEndPoint _targetEndpoint;
    private AvatarVariantMapFile _mapping;
    private string _parameterAddress;
    private string? _currentAvatarId;
    private int? _lastObservedValue;
    private FileSystemWatcher? _watcher;

    public AvatarVariantOscBridge(BridgeOptions options)
    {
        _options = options;
        _mapping = LoadMapping(options.MappingPath);
        _parameterAddress = BuildParameterAddress(_mapping.ParameterName);
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, options.ListenPort));
        _sender = new UdpClient();
        _targetEndpoint = new IPEndPoint(IPAddress.Parse(options.Host), options.SendPort);
        TryCreateWatcher();
    }

    public async Task RunAsync()
    {
        Console.WriteLine($"Mapping: {_options.MappingPath}");
        Console.WriteLine($"Listening: 0.0.0.0:{_options.ListenPort}");
        Console.WriteLine($"Sending: {_options.Host}:{_options.SendPort}");
        Console.WriteLine($"Parameter: {_mapping.ParameterName}");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await _listener.ReceiveAsync(cts.Token);
                if (!OscCodec.TryReadMessage(result.Buffer, out var message))
                {
                    continue;
                }

                HandleMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _watcher?.Dispose();
            _listener.Dispose();
            _sender.Dispose();
        }
    }

    private void HandleMessage(OscMessage message)
    {
        if (string.Equals(message.Address, "/avatar/change", StringComparison.Ordinal))
        {
            if (message.Arguments.Count > 0 && message.Arguments[0] is string avatarId)
            {
                _currentAvatarId = avatarId;
                Console.WriteLine($"Current avatar: {avatarId}");
            }

            return;
        }

        if (!string.Equals(message.Address, _parameterAddress, StringComparison.Ordinal))
        {
            return;
        }

        if (!TryReadParameterValue(message, out var parameterValue))
        {
            return;
        }

        if (_lastObservedValue == parameterValue)
        {
            return;
        }

        _lastObservedValue = parameterValue;

        var mapped = _mapping.Entries.FirstOrDefault(entry => entry.Value == parameterValue);
        if (mapped == null)
        {
            Console.WriteLine($"No avatar mapped for value {parameterValue}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(mapped.BlueprintId))
        {
            Console.WriteLine($"Value {parameterValue} is mapped, but blueprintId is empty.");
            return;
        }

        if (string.Equals(mapped.BlueprintId, _currentAvatarId, StringComparison.Ordinal))
        {
            Console.WriteLine($"Value {parameterValue} already matches current avatar.");
            return;
        }

        var packet = OscCodec.WriteMessage("/avatar/change", mapped.BlueprintId);
        _sender.Send(packet, packet.Length, _targetEndpoint);
        _currentAvatarId = mapped.BlueprintId;
        Console.WriteLine($"Switched avatar for value {parameterValue}: {mapped.Name} -> {mapped.BlueprintId}");
    }

    private bool TryReadParameterValue(OscMessage message, out int value)
    {
        value = default;

        if (message.Arguments.Count == 0)
        {
            return false;
        }

        var argument = message.Arguments[0];
        switch (argument)
        {
            case int intValue:
                value = intValue;
                return true;
            case float floatValue:
                value = (int)MathF.Round(floatValue);
                return true;
            case bool boolValue:
                value = boolValue ? 1 : 0;
                return true;
            default:
                return false;
        }
    }

    private void TryCreateWatcher()
    {
        var directory = Path.GetDirectoryName(_options.MappingPath);
        var fileName = Path.GetFileName(_options.MappingPath);

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _watcher.Changed += (_, _) => ReloadMapping();
        _watcher.Created += (_, _) => ReloadMapping();
        _watcher.Renamed += (_, _) => ReloadMapping();
        _watcher.EnableRaisingEvents = true;
    }

    private void ReloadMapping()
    {
        try
        {
            Thread.Sleep(100);
            _mapping = LoadMapping(_options.MappingPath);
            _parameterAddress = BuildParameterAddress(_mapping.ParameterName);
            Console.WriteLine($"Reloaded mapping file: {_options.MappingPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to reload mapping: {ex.Message}");
        }
    }

    private static AvatarVariantMapFile LoadMapping(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Mapping file not found.", path);
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        var mapping = JsonSerializer.Deserialize<AvatarVariantMapFile>(json, JsonOptions.Instance);
        if (mapping == null)
        {
            throw new InvalidOperationException("Failed to deserialize mapping file.");
        }

        if (string.IsNullOrWhiteSpace(mapping.ParameterName))
        {
            throw new InvalidOperationException("Mapping file is missing parameterName.");
        }

        return mapping;
    }

    private static string BuildParameterAddress(string parameterName)
    {
        return $"/avatar/parameters/{parameterName}";
    }
}

internal sealed class BridgeOptions
{
    public string MappingPath { get; private set; } = string.Empty;
    public string Host { get; private set; } = "127.0.0.1";
    public int ListenPort { get; private set; } = 9001;
    public int SendPort { get; private set; } = 9000;

    public static BridgeOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return null;
        }

        var options = new BridgeOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--host":
                    options.Host = RequireValue(args, ref i, "--host");
                    break;
                case "--listen-port":
                    options.ListenPort = int.Parse(RequireValue(args, ref i, "--listen-port"));
                    break;
                case "--send-port":
                    options.SendPort = int.Parse(RequireValue(args, ref i, "--send-port"));
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(options.MappingPath))
                    {
                        options.MappingPath = Path.GetFullPath(arg);
                    }
                    else
                    {
                        Console.WriteLine($"Unknown argument: {arg}");
                        PrintUsage();
                        return null;
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(options.MappingPath))
        {
            PrintUsage();
            return null;
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {optionName}");
        }

        index++;
        return args[index];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project Packages/com.fiona.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -- <mapping.json> [--host 127.0.0.1] [--listen-port 9001] [--send-port 9000]");
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Instance = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class AvatarVariantMapFile
{
    public int Version { get; set; }
    public string GeneratedAtUtc { get; set; } = string.Empty;
    public string AvatarName { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    public string MenuName { get; set; } = string.Empty;
    public int DefaultValue { get; set; }
    public List<AvatarVariantMapEntry> Entries { get; set; } = new();
}

internal sealed class AvatarVariantMapEntry
{
    public int Value { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BlueprintId { get; set; } = string.Empty;
}

internal sealed class OscMessage
{
    public string Address { get; }
    public List<object> Arguments { get; }

    public OscMessage(string address, List<object> arguments)
    {
        Address = address;
        Arguments = arguments;
    }
}

internal static class OscCodec
{
    public static bool TryReadMessage(byte[] buffer, out OscMessage message)
    {
        message = null!;
        try
        {
            var offset = 0;
            var address = ReadPaddedString(buffer, ref offset);
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            var typeTag = ReadPaddedString(buffer, ref offset);
            if (string.IsNullOrWhiteSpace(typeTag) || typeTag[0] != ',')
            {
                return false;
            }

            var arguments = new List<object>();
            for (var i = 1; i < typeTag.Length; i++)
            {
                switch (typeTag[i])
                {
                    case 'i':
                        arguments.Add(ReadInt(buffer, ref offset));
                        break;
                    case 'f':
                        arguments.Add(ReadFloat(buffer, ref offset));
                        break;
                    case 's':
                        arguments.Add(ReadPaddedString(buffer, ref offset));
                        break;
                    case 'T':
                        arguments.Add(true);
                        break;
                    case 'F':
                        arguments.Add(false);
                        break;
                    default:
                        return false;
                }
            }

            message = new OscMessage(address, arguments);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static byte[] WriteMessage(string address, string value)
    {
        using var stream = new MemoryStream();
        WritePaddedString(stream, address);
        WritePaddedString(stream, ",s");
        WritePaddedString(stream, value);
        return stream.ToArray();
    }

    private static string ReadPaddedString(byte[] buffer, ref int offset)
    {
        var start = offset;
        while (offset < buffer.Length && buffer[offset] != 0)
        {
            offset++;
        }

        if (offset > buffer.Length)
        {
            throw new InvalidOperationException("Invalid OSC string.");
        }

        var value = Encoding.UTF8.GetString(buffer, start, offset - start);

        while (offset < buffer.Length && buffer[offset] == 0)
        {
            offset++;
            if (offset % 4 == 0)
            {
                break;
            }
        }

        return value;
    }

    private static int ReadInt(byte[] buffer, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static float ReadFloat(byte[] buffer, ref int offset)
    {
        var bits = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset, 4));
        offset += 4;
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static void WritePaddedString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);

        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}

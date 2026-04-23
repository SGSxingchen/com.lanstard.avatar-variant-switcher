namespace AvatarVariantOscBridge;

internal sealed class BridgeOptions
{
    public string MappingPath { get; private set; } = string.Empty;
    public string Host { get; private set; } = "127.0.0.1";
    public int ListenPort { get; private set; } = 9001;
    public int SendPort { get; private set; } = 9000;
    public bool Legacy { get; private set; }

    // Track explicit flag usage so we can warn when legacy-only flags are passed
    // without --legacy (they'd otherwise be silently ignored in OSCQuery mode).
    private bool _hostExplicit;
    private bool _listenExplicit;
    private bool _sendExplicit;

    public static BridgeOptions? Parse(string[] args)
    {
        var opts = new BridgeOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--map":
                case "--mapping":
                    opts.MappingPath = Path.GetFullPath(RequireValue(args, ref i, arg));
                    break;
                case "--host":
                    opts.Host = RequireValue(args, ref i, arg);
                    opts._hostExplicit = true;
                    break;
                case "--listen":
                case "--listen-port":
                    opts.ListenPort = int.Parse(RequireValue(args, ref i, arg));
                    opts._listenExplicit = true;
                    break;
                case "--send":
                case "--send-port":
                    opts.SendPort = int.Parse(RequireValue(args, ref i, arg));
                    opts._sendExplicit = true;
                    break;
                case "--legacy":
                    opts.Legacy = true;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return null;
                default:
                    if (string.IsNullOrWhiteSpace(opts.MappingPath) && !arg.StartsWith('-'))
                    {
                        opts.MappingPath = Path.GetFullPath(arg);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Unknown argument: {arg}");
                        PrintUsage();
                        return null;
                    }
                    break;
            }
        }

        // No explicit mapping path → fall back to settings / file picker.
        if (string.IsNullOrWhiteSpace(opts.MappingPath))
        {
            if (!TryResolveMappingPathFromSettings(out var resolved))
                return null;
            opts.MappingPath = resolved;
        }

        // Remember this path for next run, regardless of how we got here.
        PersistLastMappingPath(opts.MappingPath);

        if (!opts.Legacy && (opts._hostExplicit || opts._listenExplicit || opts._sendExplicit))
        {
            Console.Error.WriteLine(
                "WARN: --host/--listen/--send are ignored in OSCQuery mode. " +
                "Pass --legacy to use static 9000/9001 ports.");
        }

        return opts;
    }

    private static bool TryResolveMappingPathFromSettings(out string path)
    {
        path = string.Empty;

        var settings = BridgeSettings.Load();
        if (!string.IsNullOrWhiteSpace(settings.LastMappingPath) && File.Exists(settings.LastMappingPath))
        {
            path = settings.LastMappingPath!;
            Console.WriteLine($"Using last mapping file: {path}");
            return true;
        }

        Console.WriteLine("No mapping path given. Opening file picker...");
        var picked = FileDialog.PickMappingFile(
            title: "Select avatar-switch-map.json",
            initialPath: settings.LastMappingPath);

        if (string.IsNullOrWhiteSpace(picked))
        {
            Console.Error.WriteLine("No mapping file selected. Exiting.");
            return false;
        }

        path = Path.GetFullPath(picked);
        return true;
    }

    private static void PersistLastMappingPath(string path)
    {
        var settings = BridgeSettings.Load();
        if (string.Equals(settings.LastMappingPath, path, StringComparison.OrdinalIgnoreCase))
            return;
        settings.LastMappingPath = path;
        settings.Save();
    }

    private static string RequireValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {optionName}");
        index++;
        return args[index];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  AvatarVariantOscBridge                              (uses last-used mapping, or shows file picker)");
        Console.WriteLine("  AvatarVariantOscBridge <mapping.json>                [--legacy [--host 127.0.0.1] [--listen 9001] [--send 9000]]");
        Console.WriteLine("  AvatarVariantOscBridge --map <mapping.json>          [--legacy] [--host ...] [--listen ...] [--send ...]");
        Console.WriteLine();
        Console.WriteLine("Default mode uses OSCQuery (mDNS auto-discovery, dynamic UDP port).");
        Console.WriteLine("--legacy pins UDP 9001 listen / 9000 send — use only if VRChat's OSCQuery is disabled");
        Console.WriteLine("or you need to interop with a legacy OSC router.");
    }
}

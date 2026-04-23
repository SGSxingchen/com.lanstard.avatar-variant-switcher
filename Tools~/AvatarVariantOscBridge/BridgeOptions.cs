namespace AvatarVariantOscBridge;

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
                    break;
                case "--listen":
                case "--listen-port":
                    opts.ListenPort = int.Parse(RequireValue(args, ref i, arg));
                    break;
                case "--send":
                case "--send-port":
                    opts.SendPort = int.Parse(RequireValue(args, ref i, arg));
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

        if (string.IsNullOrWhiteSpace(opts.MappingPath))
        {
            PrintUsage();
            return null;
        }

        return opts;
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
        Console.WriteLine("  AvatarVariantOscBridge <mapping.json> [--host 127.0.0.1] [--listen 9001] [--send 9000]");
        Console.WriteLine("  or: dotnet run -- --map <mapping.json> [--host ...] [--listen ...] [--send ...]");
    }
}

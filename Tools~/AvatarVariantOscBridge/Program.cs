using AvatarVariantOscBridge;

var options = BridgeOptions.Parse(args);
if (options == null) return 1;

PrintBanner();

try
{
    var bridge = new VariantBridge(options);
    await bridge.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex.Message}");
    Console.Error.WriteLine(ex);
    return 2;
}

static void PrintBanner()
{
    var previous = Console.ForegroundColor;
    try
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" [!] VRChat /avatar/change only works for avatars in your FAVORITES list.");
        Console.WriteLine("     After first upload, open VRChat and ⭐ favourite every variant once,");
        Console.WriteLine("     otherwise this bridge's switch commands are silently dropped.");
        Console.WriteLine("=========================================================================");
    }
    finally
    {
        Console.ForegroundColor = previous;
    }
    Console.WriteLine();
}

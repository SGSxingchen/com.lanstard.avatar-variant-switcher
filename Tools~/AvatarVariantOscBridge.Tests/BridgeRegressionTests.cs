using System.Collections;
using System.Net;
using System.Reflection;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AvatarVariantOscBridge.Tests;

public sealed class BridgeRegressionTests : IDisposable
{
    private static readonly Assembly BridgeAssembly = Assembly.Load("AvatarVariantOscBridge");
    private readonly List<string> _tempFiles = new();
    private readonly List<object> _bridges = new();

    [Fact]
    public void MdnsResponder_flushes_pending_discovery_when_a_record_arrives_later()
    {
        var responder = Create("AvatarVariantOscBridge.MDnsResponder");
        Invoke(responder, "BrowseFor", "_osc._udp.local.");

        var instanceName = "VRChat-Client-123._osc._udp.local.";
        var hostName = "vrchat-host.local.";

        var ptrAndSrv = CreateDnsPacket(
            answers:
            [
                CreatePtrRecord("_osc._udp.local.", instanceName)
            ],
            additionals:
            [
                CreateSrvRecord(instanceName, hostName, 9000)
            ]);

        Invoke(responder, "HandlePacket", ptrAndSrv);
        Assert.False(GetDiscoveryStateFired(responder, instanceName));

        var aOnly = CreateDnsPacket(
            answers:
            [
                CreateARecord(hostName, IPAddress.Loopback)
            ]);

        Invoke(responder, "HandlePacket", aOnly);

        Assert.True(GetDiscoveryStateFired(responder, instanceName));
    }

    [Fact]
    public void OscQueryMode_retries_same_value_when_target_is_still_missing()
    {
        var bridge = CreateVariantBridge(legacy: false);

        var output = CaptureConsoleOut(() =>
        {
            var message = CreateOscMessage("/avatar/parameters/TestParam", 1);
            Invoke(bridge, "HandleMessage", message);
            Invoke(bridge, "HandleMessage", message);
        });

        Assert.Equal(2, CountOccurrences(output, "dropping switch"));
    }

    [Fact]
    public void LegacyMode_does_not_assume_current_avatar_before_echo()
    {
        var bridge = CreateVariantBridge(legacy: true);
        var message = CreateOscMessage("/avatar/parameters/TestParam", 1);

        Invoke(bridge, "HandleMessage", message);

        Assert.Null(GetFieldValue<string>(bridge, "_currentAvatarId"));
    }

    [Fact]
    public void DebugFlag_is_parsed_from_cli_aliases()
    {
        foreach (var alias in new[] { "--debug", "-v", "--verbose" })
        {
            var mapPath = WriteTempMapFile();
            var options = ParseOptions(new[] { "--map", mapPath, alias });
            Assert.True(GetPropertyValue<bool>(options!, "Debug"), $"alias '{alias}' should enable Debug");
            Assert.False(GetPropertyValue<bool>(options!, "Legacy"));
        }
    }

    [Fact]
    public void DebugFlag_defaults_off()
    {
        var mapPath = WriteTempMapFile();
        var options = ParseOptions(new[] { "--map", mapPath });
        Assert.False(GetPropertyValue<bool>(options!, "Debug"));
    }

    public void Dispose()
    {
        foreach (var bridge in _bridges)
        {
            DisposeIfPresent(bridge, "_reloadTimer");
            DisposeIfPresent(bridge, "_watcher");
            DisposeIfPresent(bridge, "_listener");
            DisposeIfPresent(bridge, "_sender");
            DisposeIfPresent(bridge, "_oscQuery");
        }

        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private object CreateVariantBridge(bool legacy)
    {
        var mapPath = WriteTempMapFile();
        var args = legacy
            ? new[] { "--legacy", "--map", mapPath }
            : new[] { "--map", mapPath };

        var options = ParseOptions(args);
        Assert.NotNull(options);

        var bridgeType = GetTypeOrThrow("AvatarVariantOscBridge.VariantBridge");
        var bridge = Activator.CreateInstance(bridgeType, options!)!;
        _bridges.Add(bridge);
        return bridge;
    }

    private static object? ParseOptions(string[] args)
    {
        var optionsType = GetTypeOrThrow("AvatarVariantOscBridge.BridgeOptions");
        return optionsType
            .GetMethod("Parse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { args });
    }

    private string WriteTempMapFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"avs-map-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
{
  "schemaVersion": 1,
  "generatedAtUtc": "2026-04-23T00:00:00Z",
  "parameterName": "TestParam",
  "menuName": "Test Menu",
  "defaultValue": 0,
  "variants": [
    {
      "variantKey": "variant-1",
      "paramValue": 1,
      "displayName": "Variant 1",
      "blueprintId": "avtr_variant_1"
    }
  ]
}
""");
        _tempFiles.Add(path);
        return path;
    }

    private static object CreateOscMessage(string address, object argument)
    {
        var messageType = GetTypeOrThrow("AvatarVariantOscBridge.OscMessage");
        return Activator.CreateInstance(messageType, address, new List<object> { argument })!;
    }

    private static object CreateDnsPacket(IEnumerable<object>? answers = null, IEnumerable<object>? additionals = null)
    {
        var packet = Create("AvatarVariantOscBridge.DnsPacket");
        AddRecords(packet, "Answers", answers);
        AddRecords(packet, "Additionals", additionals);
        return packet;
    }

    private static object CreatePtrRecord(string serviceType, string instanceName)
    {
        var record = Create("AvatarVariantOscBridge.DnsRecord");
        SetProperty(record, "Name", serviceType);
        SetProperty(record, "Type", GetDnsConstant("TypePtr"));
        SetProperty(record, "Class", GetDnsConstant("ClassIn"));
        SetProperty(record, "Ttl", 4500u);
        SetField(record, "PtrName", instanceName);
        return record;
    }

    private static object CreateSrvRecord(string instanceName, string hostName, ushort port)
    {
        var srv = Create("AvatarVariantOscBridge.DnsSrvData");
        SetField(srv, "Priority", (ushort)0);
        SetField(srv, "Weight", (ushort)0);
        SetField(srv, "Port", port);
        SetField(srv, "Target", hostName);

        var record = Create("AvatarVariantOscBridge.DnsRecord");
        SetProperty(record, "Name", instanceName);
        SetProperty(record, "Type", GetDnsConstant("TypeSrv"));
        SetProperty(record, "Class", GetDnsConstant("ClassIn"));
        SetProperty(record, "Ttl", 120u);
        SetField(record, "Srv", srv);
        return record;
    }

    private static object CreateARecord(string hostName, IPAddress address)
    {
        var record = Create("AvatarVariantOscBridge.DnsRecord");
        SetProperty(record, "Name", hostName);
        SetProperty(record, "Type", GetDnsConstant("TypeA"));
        SetProperty(record, "Class", GetDnsConstant("ClassIn"));
        SetProperty(record, "Ttl", 120u);
        SetField(record, "Address", address);
        return record;
    }

    private static bool GetDiscoveryStateFired(object responder, string instanceName)
    {
        var cache = (IDictionary)GetFieldValue<object>(responder, "_discoveryCache")!;
        var state = cache[instanceName]!;
        return GetFieldValue<bool>(state, "Fired");
    }

    private static void AddRecords(object packet, string propertyName, IEnumerable<object>? records)
    {
        if (records == null)
            return;

        var list = (IList)packet.GetType().GetProperty(propertyName)!.GetValue(packet)!;
        foreach (var record in records)
            list.Add(record);
    }

    private static string CaptureConsoleOut(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while (true)
        {
            index = text.IndexOf(value, index, StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            index += value.Length;
        }
    }

    private static object Create(string typeName)
        => Activator.CreateInstance(GetTypeOrThrow(typeName))!;

    private static Type GetTypeOrThrow(string typeName)
        => BridgeAssembly.GetType(typeName, throwOnError: true)!;

    private static object? Invoke(object instance, string methodName, params object[] args)
        => instance.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(instance, args);

    private static void SetProperty(object instance, string name, object? value)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private static void SetField(object instance, string name, object? value)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private static T? GetFieldValue<T>(object instance, string name)
        => (T?)instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance);

    private static T GetPropertyValue<T>(object instance, string name)
        => (T)instance.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static ushort GetDnsConstant(string name)
        => (ushort)GetTypeOrThrow("AvatarVariantOscBridge.DnsCodec")
            .GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static void DisposeIfPresent(object instance, string fieldName)
    {
        if (GetFieldValue<object>(instance, fieldName) is IDisposable disposable)
            disposable.Dispose();
    }
}

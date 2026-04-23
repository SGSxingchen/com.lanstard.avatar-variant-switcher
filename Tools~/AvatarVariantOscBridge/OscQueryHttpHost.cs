using System.Net;
using System.Text;
using System.Text.Json;

namespace AvatarVariantOscBridge;

/// <summary>
/// Tiny HTTP server that serves the OSCQuery JSON schema for our local OSC
/// endpoints. Responds to:
/// - GET /?HOST_INFO        → host info JSON
/// - GET /{path}            → sub-tree at that path, or 404 if missing
/// - Anything else          → 405 / 404
/// </summary>
internal sealed class OscQueryHttpHost : IDisposable
{
    private readonly int _port;
    private readonly Func<OscQueryNode> _treeFactory;
    private readonly Func<OscQueryHostInfo> _hostInfoFactory;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<string>? LogWarn;

    public OscQueryHttpHost(int port, Func<OscQueryNode> treeFactory, Func<OscQueryHostInfo> hostInfoFactory)
    {
        _port = port;
        _treeFactory = treeFactory;
        _hostInfoFactory = hostInfoFactory;
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private void HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;
            if (req.HttpMethod != "GET")
            {
                res.StatusCode = 405;
                res.Close();
                return;
            }

            var path = req.Url?.AbsolutePath ?? "/";
            var query = req.Url?.Query ?? string.Empty;

            if (query.Contains("HOST_INFO", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(res, JsonSerializer.Serialize(_hostInfoFactory(), BridgeJsonContext.Default.OscQueryHostInfo));
                return;
            }

            var root = _treeFactory();
            var node = FindNode(root, path);
            if (node == null)
            {
                res.StatusCode = 404;
                var msg = Encoding.UTF8.GetBytes($"No OSC method found at path {path}");
                res.OutputStream.Write(msg, 0, msg.Length);
                res.Close();
                return;
            }

            WriteJson(res, JsonSerializer.Serialize(node, BridgeJsonContext.Default.OscQueryNode));
        }
        catch (Exception ex)
        {
            LogWarn?.Invoke($"OSCQuery HTTP handler error: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private static void WriteJson(HttpListenerResponse res, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = 200;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    private static OscQueryNode? FindNode(OscQueryNode root, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return root;

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var seg in segments)
        {
            if (current.Contents == null) return null;
            if (!current.Contents.TryGetValue(seg, out var child)) return null;
            current = child;
        }
        return current;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts?.Dispose();
    }
}

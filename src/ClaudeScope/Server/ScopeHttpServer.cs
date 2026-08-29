using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeScope.Core;

namespace ClaudeScope.Server;

/// <summary>
/// 本机 HTTP 服务。两类调用方：
///   - Claude 的 hook（POST /hook，无需 token —— 它没法带）
///   - 控制台页面（读接口开放，写接口要 token）
/// 只监听 127.0.0.1，不需要管理员权限（HttpListener 对 loopback 有例外）。
/// </summary>
public sealed class ScopeHttpServer : IDisposable
{
    readonly ScopeConfig _cfg;
    readonly ScopeRegistry _registry;
    readonly ScopeLogger _log;
    readonly HttpListener _listener = new();
    CancellationTokenSource? _cts;

    /// 每次启动随机生成。控制台页面在服务端被注入这个值，
    /// 别的网页拿不到 —— 加上自定义头会触发 CORS 预检而我们不应答，双保险。
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// 控制台按钮的处理器，阶段 4 接进来
    public Func<string, JsonElement, Task<object>>? ControlHandler { get; set; }
    public Func<UsageView?>? UsageProvider { get; set; }

    public ScopeHttpServer(ScopeConfig cfg, ScopeRegistry registry, ScopeLogger log)
    {
        _cfg = cfg;
        _registry = registry;
        _log = log;
    }

    public bool Start()
    {
        _listener.Prefixes.Add($"http://{_cfg.Host}:{_cfg.Port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // 端口被占是最常见的启动失败，给出能直接照做的排查命令
            _log.Warn($"端口 {_cfg.Port} 起不来：{ex.Message}");
            _log.Warn($"排查：netstat -ano | findstr :{_cfg.Port}");
            _log.Warn($"换端口：claude-scope --install --port {_cfg.Port + 1}");
            Console.Error.WriteLine($"[claude-scope] 端口 {_cfg.Port} 被占用或无法监听，已退出");
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info($"claude-scope 已启动 http://{_cfg.Host}:{_cfg.Port}  pid={Environment.ProcessId}");
        return true;
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.Throttled("accept", "warn", $"接受连接失败：{ex.Message}");
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleAsync(ctx); }
                catch (Exception ex) { _log.Throttled("handle", "warn", $"处理请求出错：{ex.Message}"); }
                finally { try { ctx.Response.Close(); } catch { } }
            }, ct);
        }
    }

    /* ---------------------------------------------------------------- 工具 */

    static async Task WriteJsonAsync(HttpListenerContext ctx, int code, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    static async Task WriteTextAsync(HttpListenerContext ctx, int code, string text, string mime = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = mime;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    static async Task<string> ReadBodyAsync(HttpListenerContext ctx, int limit = 2 * 1024 * 1024)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n, total = 0;
        while ((n = await ctx.Request.InputStream.ReadAsync(buf)) > 0)
        {
            total += n;
            if (total > limit) break;
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    bool Authed(HttpListenerContext ctx)
    {
        var t = ctx.Request.Headers["x-scope-token"];
        if (t is null || t.Length != Token.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(t), Encoding.ASCII.GetBytes(Token));
    }

    /* ---------------------------------------------------------------- 路由 */

    async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        // ---- hook 入口：必须最快返回，它卡住会拖慢 Claude 的每一次工具调用 ----
        if (method == "POST" && path is "/hook" or "/hooks")
        {
            var body = await ReadBodyAsync(ctx);
            await WriteJsonAsync(ctx, 200, new { });
            try
            {
                using var doc = JsonDocument.Parse(body);
                _registry.ApplyHook(doc.RootElement);
                _registry.ClearIssue("hook-bad-payload");
            }
            catch (JsonException ex)
            {
                _log.Throttled("hook-parse", "warn", $"hook 负载解析失败：{ex.Message}");
                _registry.RaiseIssue("hook-bad-payload", "hook 送来的数据解析不了，该会话状态可能不准");
            }
            return;
        }

        if (path is "/api/state" or "/state")
        {
            var snap = _registry.Snapshot();
            snap.Usage = UsageProvider?.Invoke();
            await WriteJsonAsync(ctx, 200, snap);
            return;
        }

        if (path == "/health")
        {
            await WriteJsonAsync(ctx, 200, new
            {
                ok = true,
                pid = Environment.ProcessId,
                port = _cfg.Port,
                startedAt = new DateTimeOffset(_registry.StartedAt).ToUnixTimeMilliseconds()
            });
            return;
        }

        if (path == "/api/control" && method == "POST")
        {
            if (!Authed(ctx)) { await WriteJsonAsync(ctx, 403, new { ok = false, message = "token 不对" }); return; }
            if (ControlHandler is null) { await WriteJsonAsync(ctx, 503, new { ok = false, message = "控制接口未就绪" }); return; }

            var body = await ReadBodyAsync(ctx, 256 * 1024);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException ex) { await WriteJsonAsync(ctx, 400, new { ok = false, message = $"请求体不是合法 JSON：{ex.Message}" }); return; }

            using (doc)
            {
                var action = doc.RootElement.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                    ? a.GetString()! : "";
                var result = await ControlHandler(action, doc.RootElement);
                _log.Info($"控制台动作 {action}");
                await WriteJsonAsync(ctx, 200, result);
            }
            return;
        }

        if (method == "GET")
        {
            if (path == "/")
            {
                // 界面在横幅的右键菜单和设置窗里，这个端口只是给 hook 和 CLI 用的。
                // 直接把有哪些端点写出来，比 302 到一个不存在的页面诚实。
                await WriteTextAsync(ctx, 200,
                    "claude-scope\n\n"
                    + "GET  /health       进程是否活着\n"
                    + "GET  /api/state    当前状态快照（JSON）\n"
                    + "POST /hook         Claude Code 的 hook 往这里发\n"
                    + "POST /api/control  控制接口，需要 x-scope-token\n\n"
                    + "界面在横幅上点右键，或者双击托盘图标。\n");
                return;
            }

            // 未知路径老实返回 404——全都 302 会让"这个端点还在不在"变得看不出来。
            await WriteTextAsync(ctx, 404, "not found");
            return;
        }

        await WriteTextAsync(ctx, 405, "method not allowed");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        _listener.Close();
    }
}

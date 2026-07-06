using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Necroking.Core;

namespace Necroking.Dev;

/// <summary>
/// A single command received over the dev HTTP channel. Created on the HTTP
/// listener thread, executed on the game's main thread (so it can safely touch
/// the Simulation / renderer), then completed to unblock the HTTP response.
/// </summary>
public sealed class DevCommand
{
    public string Cmd = "";
    public string[] Args = Array.Empty<string>();

    /// <summary>Named options from the request's "opts" object (extensible — e.g.
    /// screenshot's no_ui / no_ground / downsample_to). Values normalised to strings.</summary>
    public Dictionary<string, string> Opts = new(StringComparer.OrdinalIgnoreCase);

    public bool OptBool(string key) => Opts.TryGetValue(key, out var v) && (v == "true" || v == "1");
    public string? Opt(string key) => Opts.TryGetValue(key, out var v) ? v : null;

    private readonly TaskCompletionSource<string> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> Result => _tcs.Task;

    /// <summary>Complete with a raw JSON response body. Safe to call once.</summary>
    public void Complete(string json) => _tcs.TrySetResult(json);

    /// <summary>Build a command from a JSON object that may carry "cmd", "args"
    /// (array of strings/numbers) and "opts" (object). Shared by the HTTP parser
    /// and the batch-script runner so a step in a batch behaves identically to a
    /// stand-alone command. <paramref name="fallbackCmd"/> is used when the object
    /// has no "cmd" field (e.g. the URL path shortcut).</summary>
    public static DevCommand FromElement(JsonElement root, string fallbackCmd = "")
    {
        string cmd = root.TryGetProperty("cmd", out var c) ? (c.GetString() ?? fallbackCmd) : fallbackCmd;
        var result = new DevCommand { Cmd = cmd };

        if (root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            int n = a.GetArrayLength();
            var args = new string[n];
            int i = 0;
            foreach (var el in a.EnumerateArray())
                args[i++] = el.ValueKind == JsonValueKind.String
                    ? (el.GetString() ?? "")
                    : el.GetRawText();
            result.Args = args;
        }

        if (root.TryGetProperty("opts", out var o) && o.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in o.EnumerateObject())
                result.Opts[p.Name] = NormalizeOptValue(p.Value);
        }
        return result;
    }

    /// <summary>Normalise a JSON opt value to the string form Opts stores
    /// (bools → "true"/"false", numbers → raw text).</summary>
    public static string NormalizeOptValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(),
    };
}

/// <summary>
/// Lean in-process control channel for the dev workflow. Listens on
/// http://localhost:&lt;port&gt;/ (localhost only) for JSON commands, queues them,
/// and lets the game drain + execute them on the main thread. Only started when
/// the game is launched with --devserver &lt;port&gt;; never active in normal play.
///
/// Transport only — this class knows nothing about game state. Game1 supplies
/// the executor via Drain(), so all world mutation rides Game1's own APIs (the
/// same primitives scenarios use) rather than a parallel implementation.
///
/// Request:  POST /  body: {"cmd":"spawn","args":["Skeleton",10,10]}
/// Response: {"ok":true,"result":"..."} or {"ok":false,"error":"..."}
/// </summary>
public sealed class DevServer
{
    public const string LogCat = "devserver";

    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<DevCommand> _queue = new();
    private volatile bool _running;

    public int Port { get; }

    public DevServer(int port)
    {
        Port = port;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogCat, $"FAILED to start on port {Port}: {ex.Message}");
            return;
        }
        _running = true;
        var t = new Thread(ListenLoop) { IsBackground = true, Name = "DevServer" };
        t.Start();
        DebugLog.Log(LogCat, $"listening on http://localhost:{Port}/");
    }

    public void Stop()
    {
        _running = false;
        try { _listener.Stop(); } catch { /* already down */ }
    }

    /// <summary>Main-thread pump: run every queued command through
    /// <paramref name="execute"/>. The executor is responsible for completing
    /// each command (immediately, or later for deferred ops like screenshots).</summary>
    public void Drain(Action<DevCommand> execute)
    {
        while (_queue.TryDequeue(out var cmd))
        {
            try { execute(cmd); }
            catch (Exception ex) { cmd.Complete(Error(ex.Message)); }
        }
    }

    // --- HTTP listener thread ---

    private void ListenLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; } // listener stopped / disposed
            ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = r.ReadToEnd();

            var cmd = Parse(ctx.Request.Url?.AbsolutePath ?? "/", body);
            if (cmd == null) { Respond(ctx, 400, Error("could not parse command")); return; }

            _queue.Enqueue(cmd);

            // Block the HTTP response until the main thread has executed it.
            string result = cmd.Result.Wait(15000)
                ? cmd.Result.Result
                : Error("timeout waiting for game main thread");
            Respond(ctx, 200, result);
        }
        catch (Exception ex)
        {
            try { Respond(ctx, 500, Error(ex.Message)); } catch { /* socket gone */ }
        }
    }

    /// <summary>Accepts either a JSON body {"cmd":..,"args":[..]} or, as a
    /// convenience, the URL path as the command (GET /ping). Args elements may be
    /// strings or numbers; both are normalised to strings for the executor.</summary>
    private static DevCommand? Parse(string path, string body)
    {
        // Path shortcut: anything other than "/" with an empty body is the command.
        string trimmedPath = path.Trim('/');
        if (string.IsNullOrWhiteSpace(body))
        {
            if (string.IsNullOrEmpty(trimmedPath)) return null;
            return new DevCommand { Cmd = trimmedPath };
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var cmd = DevCommand.FromElement(doc.RootElement, trimmedPath);
            return string.IsNullOrEmpty(cmd.Cmd) ? null : cmd;
        }
        catch
        {
            return null;
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    // --- JSON response helpers (also used by Game1's executor) ---

    public static string Ok(string result) =>
        $"{{\"ok\":true,\"result\":{JsonSerializer.Serialize(result)}}}";

    /// <summary>Wrap an already-formed JSON object/value as the result payload.</summary>
    public static string OkRaw(string resultJson) =>
        $"{{\"ok\":true,\"result\":{resultJson}}}";

    public static string Error(string msg) =>
        $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(msg)}}}";
}

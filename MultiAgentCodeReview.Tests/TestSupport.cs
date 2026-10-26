using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MultiAgentCodeReview.Tests;

/// <summary>A throw-away git repository for tests that need real commits and diffs.</summary>
internal sealed class TempRepo : IDisposable
{
    public string Path { get; }

    public TempRepo()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mar-test-" + Guid.NewGuid());
        Directory.CreateDirectory(Path);
        Git("init", "-q", "-b", "main");
    }

    public string Git(params string[] args) => RunGit(Path, args);

    public static string RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = "test"; psi.Environment["GIT_AUTHOR_EMAIL"] = "test@example.com";
        psi.Environment["GIT_COMMITTER_NAME"] = "test"; psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
        psi.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null"; // ignore the developer's own git config
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        _ = stderr.Result;
        return stdout.Result.Trim();
    }

    public void Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void WriteBytes(string relative, byte[] content) => File.WriteAllBytes(System.IO.Path.Combine(Path, relative), content);

    public void Delete(string relative) => File.Delete(System.IO.Path.Combine(Path, relative));

    public string Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

internal sealed record LlmRequest(string System, string User, string Model, string Authorization);

/// <summary>
/// A tiny OpenAI-compatible chat-completions server, so the real pipeline (AutoGen + the OpenAI SDK) can be run
/// end to end without any network or API key. What it answers is decided by <see cref="Responder"/>.
/// </summary>
internal sealed class FakeLlm : IDisposable
{
    private readonly HttpListener _listener = new();
    public string BaseUrl { get; }
    public List<LlmRequest> Requests { get; } = new();
    public Func<string, string, string> Responder { get; set; } = (_, _) => "ok";

    public FakeLlm()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try { Handle(await _listener.GetContextAsync()); } catch { /* listener closed */ }
            }
        });
    }

    private static string Text(JsonElement content) =>
        content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));

    private void Handle(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream)) body = reader.ReadToEnd();
        using var doc = JsonDocument.Parse(body);
        string system = "", user = "";
        foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            var role = m.GetProperty("role").GetString();
            var text = Text(m.GetProperty("content"));
            if (role == "system") system = text; else if (role == "user") user = text;
        }
        var model = doc.RootElement.GetProperty("model").GetString()!;
        lock (Requests) Requests.Add(new LlmRequest(system, user, model, ctx.Request.Headers["Authorization"] ?? ""));

        var content = Responder(system, user);
        var response = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test", @object = "chat.completion", created = 1, model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
        });
        var bytes = Encoding.UTF8.GetBytes(response);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose() => _listener.Close();
}

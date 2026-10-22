using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Bilgames.Core.Assist;

namespace Bilgames.Assist.Llama;

/// <summary>
/// The assistant's model as an <see cref="IChatModel"/>: starts llama.cpp's server when it's first
/// needed, sends requests in llama.cpp's OpenAI-compatible format with a JSON schema (so answers always
/// parse), and stops the server after a few idle minutes to give the memory back.
/// </summary>
public sealed class LocalAssistantModel : IChatModel, IAsyncDisposable
{
    private readonly AssistantDownloader _install;
    private readonly Func<string?> _preferredDevice;
    private readonly string? _logFile;
    private readonly TimeSpan _idle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LlamaServer? _server;
    private HttpClient? _http;
    private DateTime _lastUse;
    private Timer? _idleTimer;

    public const int ContextSize = 16384;

    /// <summary>Raised when the server starts or stops ("Loading the model…", "Ready", "Stopped").</summary>
    public event Action<string>? StateChanged;

    public LocalAssistantModel(AssistantDownloader install, Func<string?> preferredDevice, string? logFile, TimeSpan? idleTimeout = null)
    {
        _install = install;
        _preferredDevice = preferredDevice;
        _logFile = logFile;
        _idle = idleTimeout ?? TimeSpan.FromMinutes(5);
    }

    public bool Running => _server is { HasExited: false };

    public async Task<JsonNode> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, int maxTokens, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var http = await EnsureServerAsync(ct);
            var body = new JsonObject
            {
                ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
                ["temperature"] = 0.2,
                ["top_p"] = 0.9,
                ["max_tokens"] = maxTokens,
                ["cache_prompt"] = true,
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "answer", ["strict"] = true, ["schema"] = schema.DeepClone() },
                },
            };
            HttpResponseMessage response;
            try { response = await http.PostAsJsonAsync("v1/chat/completions", body, ct); }
            catch (HttpRequestException ex)
            {
                var reason = _server is { HasExited: true } s ? LlamaServer.Explain(s.Tail) : "The assistant didn't answer.";
                await StopAsync();
                throw new AssistantUnavailableException(reason, ex);
            }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new AssistantUnavailableException($"The assistant couldn't answer ({(int)response.StatusCode}): {Short(text)}");
                var choice = JsonNode.Parse(text)?["choices"]?[0];
                var content = choice?["message"]?["content"]?.GetValue<string>();
                if (choice?["finish_reason"]?.GetValue<string>() == "length")
                    throw new AssistantAnswerTooLongException("The assistant's answer was longer than its budget.");
                if (string.IsNullOrWhiteSpace(content)) throw new AssistantUnavailableException("The assistant gave an empty answer.");
                try { return JsonNode.Parse(content) ?? new JsonObject(); }
                catch (System.Text.Json.JsonException) { throw new AssistantAnswerTooLongException("The assistant's answer was cut off."); }
            }
        }
        finally
        {
            _lastUse = DateTime.UtcNow;
            _gate.Release();
        }
    }

    private async Task<HttpClient> EnsureServerAsync(CancellationToken ct)
    {
        if (_server is { HasExited: false } && _http is not null) return _http;
        await StopAsync();
        if (!_install.Installed) throw new AssistantUnavailableException("The assistant's model isn't downloaded yet (Settings › Review assistant).");
        StateChanged?.Invoke("Loading the model…");
        var devices = await LlamaServer.ListDevicesAsync(_install.ServerExe, ct);
        var device = LlamaServer.ChooseDevice(devices, _preferredDevice());
        _server = await LlamaServer.StartAsync(_install.ServerExe, _install.ModelPath, device, ContextSize, _logFile, ct);
        _http = new HttpClient { BaseAddress = _server.Address, Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _server.ApiKey);
        _idleTimer ??= new Timer(_ => _ = StopIfIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        StateChanged?.Invoke(device is null ? "Ready" : $"Ready · {device.Name}");
        return _http;
    }

    private async Task StopIfIdleAsync()
    {
        if (_server is null || DateTime.UtcNow - _lastUse < _idle) return;
        if (!await _gate.WaitAsync(0)) return; // busy
        try { if (DateTime.UtcNow - _lastUse >= _idle) await StopAsync(); }
        finally { _gate.Release(); }
    }

    /// <summary>Stops the server now (frees its memory). The next request starts it again.</summary>
    public async Task StopAsync()
    {
        var server = _server;
        _server = null;
        _http?.Dispose();
        _http = null;
        if (server is not null)
        {
            await server.DisposeAsync();
            StateChanged?.Invoke("Stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _idleTimer?.Dispose();
        await StopAsync();
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200] + "…";
}

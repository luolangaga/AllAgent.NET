using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;

namespace LanAgent.CommandCode;

// Protocol ported from MAXeaglet/commandcode-proxy (MIT); see Upstream/LICENSE.
public sealed class CommandCodeProvider(CommandCodeAccount account, HttpClient http) : IAiProvider
{
    private const string Version = "1.53.1";
    private const string WorkingDir = @"C:\Users\dev\projects\app";
    private readonly SemaphoreSlim initialization = new(1, 1);
    private string? initializedKey;
    private DateTimeOffset nextInitialization;
    public string ProviderId => account.Id;
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default)
    {
        account.ValidateModelConfiguration(config);
        return await account.FetchModelsAsync(await account.GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("请先 /login commandcode。"), ct);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static HttpRequestMessage Request(string path, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CommandCodeAccount.Endpoint + path)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("User-Agent", "cli");
        request.Headers.Add("x-cli-environment", "production");
        request.Headers.Add("x-command-code-version", Version);
        return request;
    }
    private async Task InitializeAsync(string token, CancellationToken ct)
    {
        await initialization.WaitAsync(ct);
        try
        {
            var key = Hash(token);
            if (key == initializedKey && DateTimeOffset.UtcNow < nextInitialization) return;
            // Stable synthetic device; no local hardware, user name or project paths leave the host.
            var machine = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("\0" + token + "\0machineId"))[..16]).ToString();
            var mac = string.Join(":", Enumerable.Range(0, 6).Select(i => Hash("\0" + token + "\0mac0").Substring(i * 2, 2)));
            string Signal(string value) => Hash("command-code:device-fingerprint:v1\0" + value.ToLowerInvariant());
            var fingerprint = new
            {
                thumbmark = Hash("command-code:device-fingerprint:v1\0machine\0" + machine + "|" + mac),
                components = new { machineIdHash = Signal(machine), macHashes = new[] { Signal(mac) }, osUserHash = Signal("dev"), hostnameHash = Signal("DESKTOP-" + key[..8]), gitEmailHash = Signal("dev." + key[..6] + "@gmail.com"), platform = "win32", arch = "x64", osRelease = "10.0.22631", cpuModel = "Intel(R) Core(TM) i7-12700K", cpuCount = 20, memGiB = 32, isContainer = false, timezone = "Asia/Shanghai", runtime = "cli", collectorVersion = 1 }
            };
            foreach (var (path, body) in new (string, object)[]
            {
                ("/alpha/fingerprint/record", fingerprint),
                ("/alpha/lifecycle-events", new { eventType = "cli_session_exists", metadata = new { sessionId = "sess_" + Guid.NewGuid().ToString("N")[..16], cliVersion = Version, mode = "interactive", os = "win32-x64" } })
            })
            {
                using var request = Request(path, token, body);
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("CommandCode 初始化失败：HTTP " + (int)response.StatusCode);
            }
            initializedKey = key;
            nextInitialization = DateTimeOffset.UtcNow.AddHours(8);
        }
        finally { initialization.Release(); }
    }
    public static JsonObject BuildRequest(ChatRequest request)
    {
        var names = new Dictionary<string, string>();
        foreach (var message in request.Messages.Where(m => m.ToolCallsJson != null))
            foreach (var call in JsonNode.Parse(message.ToolCallsJson!)!.AsArray())
                names[call!["id"]!.GetValue<string>()] = call["function"]!["name"]!.GetValue<string>();
        var system = new JsonArray();
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.Role is "system" or "developer")
            { system.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content }); continue; }
            var parts = new JsonArray();
            if (message.Role == "tool")
                parts.Add(new JsonObject { ["type"] = "tool-result", ["toolCallId"] = message.ToolCallId, ["toolName"] = names.GetValueOrDefault(message.ToolCallId ?? "") ?? message.ToolName ?? "", ["output"] = new JsonObject { ["type"] = "text", ["value"] = message.Content } });
            else
            {
                if (!string.IsNullOrEmpty(message.ReasoningContent)) parts.Add(new JsonObject { ["type"] = "reasoning", ["text"] = message.ReasoningContent });
                if (!string.IsNullOrEmpty(message.Content)) parts.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
                ImageAttachment.Validate(message.Images);
                foreach (var image in message.Images) parts.Add(new JsonObject { ["type"] = "image", ["image"] = image.DataUrl, ["mimeType"] = image.MediaType });
                if (message.ToolCallsJson != null)
                    foreach (var call in JsonNode.Parse(message.ToolCallsJson)!.AsArray())
                    {
                        var function = call!["function"]!;
                        var args = function["arguments"]?.GetValue<string>() ?? "{}";
                        JsonNode? input;
                        try { input = JsonNode.Parse(args); } catch (JsonException) { input = JsonValue.Create(args); }
                        parts.Add(new JsonObject { ["type"] = "tool-call", ["toolCallId"] = call["id"]!.DeepClone(), ["toolName"] = function["name"]!.DeepClone(), ["input"] = input });
                    }
            }
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = parts });
        }
        for (var i = 0; i < system.Count - 1; i++) system[i]!["text"] = system[i]!["text"]!.GetValue<string>() + "\n";
        if (system.Count == 0) system.Add(new JsonObject { ["type"] = "text", ["text"] = " " });
        var tools = new JsonArray();
        foreach (var tool in request.Tools ?? []) tools.Add(new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["input_schema"] = JsonSerializer.SerializeToNode(tool.Parameters) });
        var parameters = new JsonObject { ["model"] = request.Config.DefaultModel, ["messages"] = messages, ["system"] = system, ["max_tokens"] = Math.Clamp(request.Config.MaxTokens, 1, 200000), ["stream"] = true, ["temperature"] = request.Config.Temperature, ["tools"] = tools };
        if (request.Config.ReasoningEffort != "default")
        {
            if (!ReasoningSettings.Efforts(request.Config).Contains(request.Config.ReasoningEffort)) throw new ArgumentException("当前模型不支持所选思考强度。");
            parameters["reasoning_effort"] = request.Config.ReasoningEffort;
        }
        return new JsonObject
        {
            ["config"] = JsonSerializer.SerializeToNode(new { workingDir = WorkingDir, date = DateTime.UtcNow.ToString("yyyy-MM-dd"), environment = "win32", structure = Array.Empty<string>(), isGitRepo = false, currentBranch = "", mainBranch = "", gitStatus = "", recentCommits = Array.Empty<string>() }),
            ["memory"] = null, ["taste"] = null, ["skills"] = null, ["permissionMode"] = "standard", ["threadId"] = (request.ConversationId ?? Guid.NewGuid()).ToString(), ["mode"] = "agent", ["params"] = parameters
        };
    }
    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        account.ValidateModelConfiguration(request.Config);
        var token = await account.GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("请先 /login commandcode。");
        await InitializeAsync(token, ct);
        var body = BuildRequest(request);
        using var message = Request("/alpha/generate", token, body);
        message.Headers.Add("x-project-slug", "c-users-dev-projects-app");
        message.Headers.Add("x-taste-learning", "false");
        message.Headers.Add("x-session-id", body["threadId"]!.GetValue<string>());
        message.Headers.Add("traceparent", "00-" + Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N")[..16] + "-01");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { yield return new(ChunkType.Error, ErrorMessage: "CommandCode HTTP " + (int)response.StatusCode); yield break; }
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await foreach (var chunk in ParseStreamAsync(reader, ct)) yield return chunk;
    }
    public static async IAsyncEnumerable<StreamChunk> ParseStreamAsync(TextReader reader, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var calls = new List<ToolCallChunk>();
        var finished = false;
        var output = false;
        var reason = "stop";
        JsonNode? usage = null;
        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(90), ct); }
            catch (TimeoutException) { line = null; }
            if (line == null) break;
            line = line.Trim();
            if (line.StartsWith("data:", StringComparison.Ordinal)) line = line[5..].TrimStart();
            if (line.Length == 0 || line.StartsWith(':') || line == "[DONE]") continue;
            JsonNode? e;
            try { e = JsonNode.Parse(line); } catch (JsonException) { e = null; }
            if (e == null) { yield return new(ChunkType.Error, ErrorMessage: "CommandCode 返回无效流数据。"); yield break; }
            var type = e?["type"]?.GetValue<string>();
            if (type is "text-delta" or "reasoning-delta")
            {
                var text = e?["text"]?.GetValue<string>() ?? e?["delta"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text)) { if (type == "text-delta") output = true; yield return new(type == "text-delta" ? ChunkType.TextDelta : ChunkType.ThinkingDelta, TextDelta: text); }
            }
            else if (type == "tool-call")
            {
                var input = e?["input"];
                var args = input is JsonValue value && value.TryGetValue<string>(out var s) ? s : input?.ToJsonString() ?? "{}";
                if (args.Length > 200000) throw new InvalidOperationException("工具参数超过限制。");
                calls.Add(new(e?["toolCallId"]?.GetValue<string>() ?? "call_" + Guid.NewGuid().ToString("N"), e?["toolName"]?.GetValue<string>() ?? "", args));
                output = true;
            }
            else if (type == "error") { yield return new(ChunkType.Error, ErrorMessage: "CommandCode 上游返回错误。"); yield break; }
            else if (type is "finish" or "finish-step")
            {
                finished = true;
                reason = e?["finishReason"]?.GetValue<string>() ?? reason;
                usage = e?["totalUsage"] ?? e?["usage"] ?? usage;
                if (type == "finish") break;
            }
        }
        if (!finished || !output || reason.Contains("error", StringComparison.OrdinalIgnoreCase))
        { yield return new(ChunkType.Error, ErrorMessage: "CommandCode 响应为空、失败或流被截断。"); yield break; }
        if (usage != null) yield return new(ChunkType.Usage) { Usage = new(usage["inputTokens"]?.GetValue<int>() ?? 0, usage["outputTokens"]?.GetValue<int>() ?? 0) { CachedInputTokens = usage["cachedInputTokens"]?.GetValue<int>() } };
        foreach (var call in calls) yield return new(ChunkType.ToolCallComplete, ToolCall: call);
        yield return new(ChunkType.Done, FinishReason: reason switch { "tool-calls" or "tool_use" or "tool_calls" => "tool_calls", "max_tokens" or "max_output_tokens" or "model_context_window_exceeded" or "length" => "length", _ => "stop" });
    }
}

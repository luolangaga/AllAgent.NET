using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanAgent.Core.Bridge;

public record BridgeRequest(string Id, string Method, JsonElement Params);
public record BridgeResponse(string Id, string Type, object? Data = null, string? Error = null, string? EventName = null);

public class BridgeDispatcher
{
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _handlers = new();

    public void Register<TParams, TResult>(string method, Func<TParams, CancellationToken, Task<TResult>> handler)
    {
        _handlers[method] = async (json, ct) =>
        {
            var p = json.ValueKind == JsonValueKind.Undefined
                ? default
                : json.Deserialize<TParams>(JsonOptions.Default);
            var result = await handler(p!, ct);
            return (object?)result;
        };
    }

    public void Register<TResult>(string method, Func<CancellationToken, Task<TResult>> handler)
    {
        _handlers[method] = async (_, ct) => (object?)await handler(ct);
    }

    public async Task<string> InvokeAsync(string requestJson)
    {
        BridgeRequest req;
        try { req = JsonSerializer.Deserialize<BridgeRequest>(requestJson, JsonOptions.Default)!; }
        catch (Exception ex)
        {
            var err = $"Invalid request JSON: {ex.Message}";
            LogToFile($"[BridgeDispatcher] {err}");
            return Serialize(new BridgeResponse("", "response", Error: err));
        }

        if (!_handlers.TryGetValue(req.Method, out var handler))
        {
            var err = $"Unknown method: {req.Method}";
            LogToFile($"[BridgeDispatcher] {err} | requestId={req.Id}");
            return Serialize(new BridgeResponse(req.Id, "response", Error: err));
        }

        try
        {
            var result = await handler(req.Params, CancellationToken.None);
            var response = Serialize(new BridgeResponse(req.Id, "response", Data: result));
            LogToFile($"[BridgeDispatcher] OK {req.Method} | requestId={req.Id} | dataType={result?.GetType().Name ?? "null"} | responseLen={response.Length}");
            return response;
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (ex.InnerException != null)
                msg += " → " + ex.InnerException.Message;
            if (ex.InnerException?.InnerException != null)
                msg += " → " + ex.InnerException.InnerException.Message;
            LogToFile($"[BridgeDispatcher] ERR {req.Method} | requestId={req.Id} | {msg}");
            return Serialize(new BridgeResponse(req.Id, "response", Error: msg));
        }
    }

    private static void LogToFile(string message)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logPath = Path.Combine(appData, "LanAgent", "lanagent.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch { /* logging must never crash */ }
    }

    private static string Serialize(BridgeResponse r) => JsonSerializer.Serialize(r, JsonOptions.Default);
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

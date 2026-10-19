using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AllAgent.NET.Core.Bridge;

public record BridgeRequest(string Id, string Method, JsonElement Params);
public record BridgeResponse(string Id, string Type, object? Data = null, string? Error = null, string? EventName = null);

public class BridgeDispatcher
{
    private readonly ConcurrentDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _handlers = new();
    private readonly ConcurrentDictionary<string, string[]> _slots = new();

    /// <summary>Every registered method, so a host that keeps one dispatcher per workspace can swap them.</summary>
    public IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> Handlers => _handlers;

    /// <summary>
    /// Replaces every method previously installed under <paramref name="slot"/> while leaving unrelated
    /// registrations alone. Switching the browser to another workspace swaps the active bundle without
    /// disturbing the host-level methods (approvals, workspace controls) registered outside any slot.
    /// </summary>
    public void Install(string slot, IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> handlers)
    {
        if (_slots.TryGetValue(slot, out var previous))
            foreach (var method in previous) _handlers.TryRemove(method, out _);
        foreach (var (method, handler) in handlers) _handlers[method] = handler;
        _slots[slot] = handlers.Keys.ToArray();
    }

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

    public async Task<string> InvokeAsync(string requestJson, CancellationToken ct = default)
    {
        BridgeRequest req;
        try
        {
            req = JsonSerializer.Deserialize<BridgeRequest>(requestJson, JsonOptions.Default)
                ?? throw new JsonException("Request must be an object.");
            if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Method))
                throw new JsonException("Request id and method are required.");
        }
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
            var result = await handler(req.Params, ct);
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
            var logPath = Path.Combine(appData, "AllAgent.NET", "allagent.log");
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

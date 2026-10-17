using System.Text.Json;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.CodeAct;

/// <summary>Host-side batching keeps concurrency independent of guest Python capabilities.</summary>
internal static class ParallelToolCalls
{
    internal static readonly AgentToolDescriptor Definition = new("parallel",
        "Prefer this for two or more ready independent host operations: batch file reads, searches, fetches and checks rather than waiting for each sequentially (max 16 calls, 8 active). Returns data.results in input order. Each call keeps its own permissions and ok/data/error/truncated result. Do not batch dependent operations. ask_user, execute_code and nested parallel calls are forbidden; the entire batch is validated before any child runs.",
        JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"properties":{"calls":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","additionalProperties":false,"properties":{"name":{"type":"string"},"arguments":{"type":"object"}},"required":["name","arguments"]}}},"required":["calls"]}
        """), ToolRisk.Read);

    internal static async ValueTask<ToolResult> ExecuteAsync(JsonElement args, HostToolCallback callback, CancellationToken ct)
    {
        JsonElement[] calls;
        try
        {
            ToolArguments.Validate(args, Definition.Parameters);
            calls = args.GetProperty("calls").EnumerateArray().Select(c => c.Clone()).ToArray();
            if (calls.Length is < 1 or > 16) throw new ArgumentException("parallel requires 1–16 calls.");
            foreach (var call in calls)
                if (string.IsNullOrWhiteSpace(call.GetProperty("name").GetString()) ||
                    call.GetProperty("name").GetString() is "parallel" or "execute_code" or "ask_user")
                    throw new ArgumentException("parallel requires ordinary host tools; nested batches, execute_code and ask_user are forbidden.");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        { return ToolResult.Failure(ex.Message); }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(8);
        var workers = calls.Select(call => Task.Run(async () =>
        {
            await slots.WaitAsync(cancellation.Token);
            try { return await callback(call.GetProperty("name").GetString()!, call.GetProperty("arguments"), cancellation.Token); }
            catch { cancellation.Cancel(); throw; }
            finally { slots.Release(); }
        }, cancellation.Token)).ToArray();
        // WhenAll joins every worker on failure/cancellation before returning to the guest.
        var results = await Task.WhenAll(workers);
        return new(results.All(r => r.Ok), new { results },
            results.Any(r => !r.Ok) ? "Some parallel calls failed; inspect data.results before retrying." : null,
            results.Any(r => r.Truncated));
    }
}

using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Terminal;

/// <summary>One bounded background refresh; render reads only the cached plain text.</summary>
internal sealed class ModelFooter(Func<IReadOnlyList<ModelFooterProvider>> providers) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private ModelFooterContext? selected;
    private string text = "";
    private long nextRefresh;
    private bool running;
    private int selectedRevision;
    public string Read(ApiConfig? config, int revision = 0)
    {
        lock (sync)
        {
            var context = config == null ? null : new ModelFooterContext(config.ProviderId, config.DefaultModel, config.AuthenticationProviderId);
            if (context != selected || revision != selectedRevision) { selected = context; selectedRevision = revision; text = ""; nextRefresh = 0; }
            if (context != null && !running && !lifetime.IsCancellationRequested && Environment.TickCount64 >= nextRefresh)
            {
                running = true;
                _ = Task.Run(() => RefreshAsync(context, revision));
            }
            return text;
        }
    }
    private async Task RefreshAsync(ModelFooterContext context, int revision)
    {
        var parts = new List<string>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            foreach (var provider in providers())
            {
                try
                {
                    if (!provider.Matches(context)) continue;
                    var items = await provider.FetchAsync(context, timeout.Token).WaitAsync(timeout.Token);
                    parts.AddRange(items.Take(8).Select(i => Clean(i.Label) + " " + Clean(i.Value)));
                }
                catch (OperationCanceledException) { break; }
                catch { parts.Add(Clean(provider.Id) + " 暂不可用"); }
            }
        }
        finally
        {
            lock (sync)
            {
                if (selected == context && selectedRevision == revision) { text = string.Join(" │ ", parts); nextRefresh = Environment.TickCount64 + 60000; }
                running = false;
            }
        }
    }
    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(160).ToArray());
    public void Dispose() { lifetime.Cancel(); }
}

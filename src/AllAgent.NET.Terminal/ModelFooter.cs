using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Terminal;

/// <summary>One bounded background refresh; render reads only the cached plain text.</summary>
internal sealed class ModelFooter(Func<IReadOnlyList<ModelFooterProvider>> providers) : IDisposable
{
    /// <summary>Cached contribution: joined text for the terminal transcript, items for hosts that render labels.</summary>
    public sealed record Snapshot(string Text, IReadOnlyList<ModelFooterItem> Items, bool Refreshing);

    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private ModelFooterContext? selected;
    private IReadOnlyList<ModelFooterItem> items = [];
    private string text = "";
    private long nextRefresh;
    private bool running;
    private int selectedRevision;

    public string Read(ApiConfig? config, int revision = 0) => ReadSnapshot(config, revision).Text;

    /// <summary>Same cache as <see cref="Read"/> plus the structured items and whether a refresh is in flight.</summary>
    public Snapshot ReadSnapshot(ApiConfig? config, int revision = 0)
    {
        lock (sync)
        {
            var context = config == null ? null : new ModelFooterContext(config.ProviderId, config.DefaultModel, config.AuthenticationProviderId);
            if (context != selected || revision != selectedRevision) { selected = context; selectedRevision = revision; items = []; text = ""; nextRefresh = 0; }
            if (context != null && !running && !lifetime.IsCancellationRequested && Environment.TickCount64 >= nextRefresh)
            {
                running = true;
                _ = Task.Run(() => RefreshAsync(context, revision));
            }
            return new Snapshot(text, items, running);
        }
    }

    private async Task RefreshAsync(ModelFooterContext context, int revision)
    {
        var collected = new List<ModelFooterItem>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            foreach (var provider in providers())
            {
                try
                {
                    if (!provider.Matches(context)) continue;
                    var fetched = await provider.FetchAsync(context, timeout.Token).WaitAsync(timeout.Token);
                    collected.AddRange(fetched.Take(8).Select(item => new ModelFooterItem(Clean(item.Label), Clean(item.Value))));
                }
                catch (OperationCanceledException) { break; }
                catch { collected.Add(new ModelFooterItem(Clean(provider.Id), "暂不可用")); }
            }
        }
        finally
        {
            lock (sync)
            {
                if (selected == context && selectedRevision == revision)
                {
                    items = collected;
                    text = string.Join(" │ ", collected.Select(item => item.Label + " " + item.Value));
                    nextRefresh = Environment.TickCount64 + 60000;
                }
                running = false;
            }
        }
    }

    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(160).ToArray());
    public void Dispose() { lifetime.Cancel(); }
}

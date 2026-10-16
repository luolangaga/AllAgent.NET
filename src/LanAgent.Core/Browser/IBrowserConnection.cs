using System.Text.Json;

namespace LanAgent.Core.Browser;

public sealed record BrowserTab(string TabId, string Url, string Title);

public interface IBrowserConnection : IAsyncDisposable
{
    string ActiveTabId { get; }
    JsonElement? Dialog { get; }
    Task<JsonElement> SendAsync(string method, object parameters, CancellationToken ct);
    Task<IReadOnlyList<BrowserTab>> TabsAsync(CancellationToken ct);
    Task<string> NewTabAsync(CancellationToken ct);
    Task SwitchTabAsync(string id, CancellationToken ct);
    Task CloseTabAsync(string id, CancellationToken ct);
}

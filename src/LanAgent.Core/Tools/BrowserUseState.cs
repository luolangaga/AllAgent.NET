using LanAgent.Core.Data;
using LanAgent.Core.Browser;

namespace LanAgent.Core.Tools;

public sealed record BrowserUseConfiguration(bool Enabled = true, string Mode = "builtin", string ExecutablePath = "", string CdpUrl = "")
{
    public string BrowserName { get; init; } = "";
    public string TabId { get; init; } = "";
    public string ProfileDirectory { get; init; } = "";
}

/// <summary>Host-owned switch shared by parent and child runtimes.</summary>
public sealed class BrowserUseState
{
    internal SemaphoreSlim ExternalGate { get; } = new(1, 1);
    private CdpConnection? _externalConnection;
    private BrowserAutomationSession? _externalSession;
    private CancellationToken _externalRevision;
    private CancellationTokenRegistration _externalRevocation;
    internal bool ExternalConnected => _externalConnection is { IsConnected: true } && !_externalRevision.IsCancellationRequested;
    internal bool AwaitingAuthorization => _externalConnection is { IsConnected: true, AwaitingTabAuthorization: true };

    // Held by the host state, not by a single LLM turn/runtime. Call under ExternalGate.
    internal async Task<(CdpConnection Connection, BrowserAutomationSession Session)> ExternalAsync(CancellationToken ct)
    {
        if (_externalConnection == null || !_externalConnection.IsConnected || _externalRevision.IsCancellationRequested)
        {
            await CloseExternalAsync();
            var snapshot = Snapshot();
            ct.ThrowIfCancellationRequested();
            _externalConnection = await CdpConnection.ConnectAsync(snapshot.Configuration.CdpUrl, ct, initialTabId: snapshot.Configuration.TabId);
            _externalSession = new BrowserAutomationSession(_externalConnection);
            _externalRevision = snapshot.Token;
            _externalRevocation = snapshot.Token.Register(_externalConnection.Abort);
        }
        return (_externalConnection, _externalSession!);
    }

    internal async Task CloseExternalAsync()
    {
        _externalRevocation.Dispose();
        if (_externalConnection != null) await _externalConnection.DisposeAsync();
        _externalConnection = null; _externalSession = null;
    }

    /// <summary>Transfer the already authorized wizard connection to the host session.</summary>
    public async Task AdoptExternalAsync(CdpConnection connection, CancellationToken ct = default)
    {
        await ExternalGate.WaitAsync(ct);
        try
        {
            await CloseExternalAsync();
            var snapshot = Snapshot();
            if (!snapshot.Configuration.Enabled) { await connection.DisposeAsync(); return; }
            _externalConnection = connection;
            _externalSession = new BrowserAutomationSession(connection);
            _externalRevision = snapshot.Token;
            _externalRevocation = snapshot.Token.Register(connection.Abort);
        }
        finally { ExternalGate.Release(); }
    }

    private readonly object _sync = new();
    private BrowserUseConfiguration _configuration = new();
    private CancellationTokenSource _revision = new();
    public string WorkerPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "browser", "LanAgent.Browser.exe");
    public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanAgent", "browser-use");
    public (BrowserUseConfiguration Configuration, CancellationToken Token) Snapshot()
    { lock (_sync) return (_configuration, _revision.Token); }

    public static void Validate(BrowserUseConfiguration value)
    {
        if (value.Mode is not ("builtin" or "chromium")) throw new ArgumentException("Browser mode must be builtin or chromium.");
        if (value.CdpUrl.Length > 0 && (!Uri.TryCreate(value.CdpUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "ws") || uri.UserInfo.Length > 0))
            throw new ArgumentException("CDP 地址必须是本机 http://127.0.0.1:端口 或 ws://127.0.0.1:端口。 ");
        if (value.ExecutablePath.Length > 0 && (!Path.IsPathFullyQualified(value.ExecutablePath) || !File.Exists(value.ExecutablePath)))
            throw new ArgumentException("浏览器可执行文件必须是存在的绝对路径。");
    }

    public void Configure(BrowserUseConfiguration value)
    {
        Validate(value);
        CancellationTokenSource old;
        lock (_sync)
        {
            if (value == _configuration) return;
            old = _revision; _revision = new(); _configuration = value;
        }
        old.Cancel(); old.Dispose();
    }

    public void Load(DataStore store) => Configure(Read(store));
    public static BrowserUseConfiguration Read(DataStore store) => new(store.GetSetting("browser:enabled") != "off",
        store.GetSetting("browser:mode") ?? "builtin", store.GetSetting("browser:executable") ?? "", store.GetSetting("browser:cdp") ?? "")
        { BrowserName = store.GetSetting("browser:name") ?? "", ProfileDirectory = store.GetSetting("browser:profile") ?? "", TabId = store.GetSetting("browser:tab") ?? "" };
    public void Save(DataStore store, BrowserUseConfiguration value)
    {
        var old = Snapshot().Configuration;
        if (value.Mode == "chromium" && value.CdpUrl == old.CdpUrl && value.BrowserName.Length == 0) value = value with { BrowserName = old.BrowserName, ProfileDirectory = old.ProfileDirectory, TabId = old.TabId };
        Configure(value);
        store.SetSetting("browser:enabled", value.Enabled ? "on" : "off");
        store.SetSetting("browser:mode", value.Mode);
        store.SetSetting("browser:executable", value.ExecutablePath);
        store.SetSetting("browser:cdp", value.CdpUrl);
        store.SetSetting("browser:name", value.BrowserName);
        store.SetSetting("browser:profile", value.ProfileDirectory);
        store.SetSetting("browser:tab", value.TabId);
    }
}

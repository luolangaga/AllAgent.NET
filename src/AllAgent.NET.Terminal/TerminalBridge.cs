using AllAgent.NET.Core.Bridge;

namespace AllAgent.NET.Terminal;

// Payloads of the browser's integrated terminal. Sessions live in WebTerminalManager; the page only
// creates tabs, forwards keystrokes and renders the pushed output frames.
public sealed record TerminalShellDto(string Id, string Label);
public sealed record TerminalCreateParams(string? Shell = null, string? Cwd = null, int Cols = 120, int Rows = 30);
public sealed record TerminalAttachResult(TerminalSessionDto Session, string Replay, long Sequence);
public sealed record TerminalInputParams(string Id, string Data);
public sealed record TerminalResizeParams(string Id, int Cols, int Rows);
public sealed record TerminalIdParams(string Id);

/// <summary>The `terminal.*` bridge group: tab CRUD, keystrokes and resize over the ConPTY sessions.</summary>
internal sealed class TerminalBridge(WebTerminalManager terminals)
{
    public void Register(BridgeDispatcher dispatcher)
    {
        dispatcher.Register<List<TerminalShellDto>>("terminal.shells", _ =>
            Task.FromResult(WebTerminalManager.Shells.Select(shell => new TerminalShellDto(shell.Id, shell.Label)).ToList()));
        dispatcher.Register<List<TerminalSessionDto>>("terminal.list", _ => Task.FromResult(terminals.List().ToList()));
        dispatcher.Register<TerminalCreateParams, TerminalSessionDto>("terminal.create", (parameters, _) =>
            Task.FromResult(terminals.Create(parameters.Shell, parameters.Cwd, parameters.Cols, parameters.Rows)));
        dispatcher.Register<TerminalIdParams, TerminalAttachResult>("terminal.attach", (parameters, _) =>
        {
            var (session, replay, sequence) = terminals.Attach(parameters.Id);
            return Task.FromResult(new TerminalAttachResult(session, replay, sequence));
        });
        dispatcher.Register<TerminalInputParams, bool>("terminal.write", async (parameters, ct) =>
        {
            if (string.IsNullOrEmpty(parameters.Data)) return true;
            if (parameters.Data.Length > 512 * 1024) throw new ArgumentException("终端输入过大。");
            return await terminals.WriteAsync(parameters.Id, Convert.FromBase64String(parameters.Data), ct);
        });
        dispatcher.Register<TerminalResizeParams, bool>("terminal.resize", (parameters, _) =>
            Task.FromResult(terminals.Resize(parameters.Id, parameters.Cols, parameters.Rows)));
        dispatcher.Register<TerminalIdParams, bool>("terminal.close", (parameters, ct) => terminals.CloseAsync(parameters.Id, ct));
    }
}

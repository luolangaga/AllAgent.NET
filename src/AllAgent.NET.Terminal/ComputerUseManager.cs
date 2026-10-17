using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Terminal;

internal sealed class ComputerUseManager(DataStore store, ComputerUseState state, TerminalUi ui)
{
    public void Load()
    {
        var mode = ComputerInteractionMode.BackgroundOnly;
        state.Configure(store.GetSetting("computer:enabled") == "on", mode);
        if (store.GetSetting("computer:endpoint") is { Length: > 0 } endpoint) state.IsolatedEndpoint = new Uri(endpoint);
        state.IsolatedTokenFile = store.GetSetting("computer:token-file");
    }
    public void Execute(IReadOnlyList<string> args)
    {
        var current = state.Snapshot();
        switch (args.FirstOrDefault() ?? "status")
        {
            case "on": case "off":
                if (args.Count != 1) throw new ArgumentException("computer on|off");
                state.Configure(args[0] == "on", current.Mode); store.SetSetting("computer:enabled", args[0]); break;
            case "mode":
                if (args.Count != 2) throw new ArgumentException("computer mode background|foreground|isolated");
                throw new ArgumentException("模式由 AI 使用 computer_mode 根据任务选择；用户使用 /computer on 或 off 控制总开关。");
            case "isolated":
                if (args.Count != 3) throw new ArgumentException("computer isolated http://127.0.0.1:端口/ Token文件路径；通过隧道连接独立 Windows 会话。");
                var endpoint = new Uri(args[1]);
                if (!endpoint.IsLoopback || endpoint.Scheme != "http" || endpoint.AbsolutePath != "/") throw new ArgumentException("隔离端点必须是 loopback HTTP 根地址；用隧道连接独立环境。");
                var file = Path.GetFullPath(args[2]);
                if (!File.Exists(file) || File.ReadAllText(file).Trim().Length < 32) throw new ArgumentException("Token 文件必须存在且至少 32 个字符。");
                state.Configure(current.Enabled, current.Mode); state.IsolatedEndpoint = endpoint; state.IsolatedTokenFile = file;
                store.SetSetting("computer:endpoint", endpoint.AbsoluteUri); store.SetSetting("computer:token-file", file); break;
            case "status": if (args.Count > 1) throw new ArgumentException("computer status"); break;
            default: throw new ArgumentException("computer on|off|status；独立环境配置：computer isolated URL Token文件。操作模式由 AI 自动选择。");
        }
        var snapshot = state.Snapshot();
        ManagementUi.Output(ui, new { enabled = snapshot.Enabled, mode = snapshot.Mode.ToString(), workerInstalled = File.Exists(state.WorkerPath), isolatedConfigured = state.IsolatedEndpoint != null && state.IsolatedTokenFile != null },
            () => ManagementUi.Success(ui, $"Computer Use：{(snapshot.Enabled ? "开启" : "关闭")} · {ComputerUsePresentation.Mode(snapshot.Mode.ToString())}。AI 自动选择模式；后台支持控件点击与文本设置，完整后台键鼠使用独立 Windows 环境。"));
    }
}

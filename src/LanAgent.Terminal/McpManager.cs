using System.Text.Json;
using System.Text.Json.Nodes;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Spectre.Console;

namespace LanAgent.Terminal;

internal sealed class McpManager(DataStore store, McpToolService service, TerminalUi ui, TerminalArguments cli)
{
    public string? PendingPrompt { get; private set; }
    public McpServerConfig Find(string name) => store.McpServerConfigs.SingleOrDefault(c => c.Id.StartsWith(name, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("服务名称或 ID 未唯一匹配。");
    private object Safe(McpServerConfig c) => new { c.Id, c.Name, c.Command, arguments = c.ArgumentList ?? CommandLineWords.Parse(c.Arguments), c.Transport, c.IsEnabled, c.TimeoutSeconds, c.AuthenticationProviderId, connected = service.IsConnected(c.Id), error = service.GetError(c.Id), environmentKeys = c.EnvironmentVariables.Keys, headerKeys = c.Headers.Keys };
    public void List()
    {
        ManagementUi.Output(ui, store.McpServerConfigs.Select(Safe).ToArray(), () =>
        {
            var table = new Table().Title("[bold]MCP 服务[/]").AddColumn("名称 / ID").AddColumn("连接 / 工具数").AddColumn("状态 / 诊断");
            foreach (var c in store.McpServerConfigs)
            {
                var count = service.GetAllServerTools().FirstOrDefault(s => s.ServerId == c.Id).Tools?.Count ?? 0;
                table.AddRow(Markup.Escape(c.Name + " · " + c.Id[..8]), Markup.Escape((service.IsConnected(c.Id) ? "已连接" : "未连接") + " · " + count), Markup.Escape((c.IsEnabled ? "启用" : "禁用") + (service.GetError(c.Id) is { } error ? "\n" + error : "")));
            }
            TerminalOutput.Write(table);
            TerminalOutput.MarkupLine("[dim]/mcp add · edit 名称 · connect/disconnect 名称 · tools 名称 · import 路径[/]");
        });
    }
    public async Task ExecuteAsync(IReadOnlyList<string> words, CancellationToken ct = default)
    {
        PendingPrompt = null;
        var action = words.FirstOrDefault() ?? (ui.Interactive && !ui.Json ? "panel" : "list");
        if (action == "panel")
        {
            List(); var choice = ManagementUi.Choose("MCP 管理", "返回", "商店", "添加", "编辑", "连接/重连", "断开", "查看工具", "启用", "禁用", "删除", "导入", "导出");
            if (choice == "返回") return;
            if (choice == "商店") { await ToolStorePage.Mcp(store, ui, [], ct); return; }
            action = choice switch { "添加" => "add", "编辑" => "edit", "连接/重连" => "connect", "断开" => "disconnect", "查看工具" => "tools", "启用" => "enable", "禁用" => "disable", "删除" => "remove", "导入" => "import", _ => "export" };
            if (action is "import" or "export") words = new[] { action, await ManagementUi.Text("JSON 文件路径") };
            else if (action != "add")
            {
                if (store.McpServerConfigs.Count == 0) throw new ArgumentException("暂无 MCP 服务。");
                var selected = ManagementUi.Choose("选择 MCP 服务", store.McpServerConfigs, c => c.Name);
                words = new[] { action, selected.Id };
            }
        }
        if (action is "store" or "search") { await ToolStorePage.Mcp(store, ui, words.Skip(1).ToArray(), ct); return; }
        if (action == "list") { List(); return; }
        if (action == "add")
        {
            var c = cli.Get("command") is { } command
                ? new McpServerConfig { Name = cli.Get("name") ?? throw new ArgumentException("需要 --name。"), Command = command, ArgumentList = JsonSerializer.Deserialize<string[]>(cli.Get("args-json") ?? "[]")!, Transport = cli.Get("transport") ?? "auto", EnvironmentVariables = JsonSerializer.Deserialize<Dictionary<string,string>>(cli.Get("env-json") ?? "{}")!, Headers = JsonSerializer.Deserialize<Dictionary<string,string>>(cli.Get("headers-json") ?? "{}")!, IsEnabled = false }
                : await WizardAsync(new McpServerConfig { IsEnabled = false });
            Validate(c); if (store.McpServerConfigs.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("服务名称已存在。");
            store.AddMcpServerConfig(c); ManagementUi.Success(ui, "服务已保存（禁用）。使用 enable 启用，connect 仅连接当前进程。"); return;
        }
        if (action == "import") { Import(ManagementUi.Require(words, 1, "配置路径")); ManagementUi.Success(ui, "配置已导入；服务默认禁用，需要逐项启用。"); return; }
        if (action == "export")
        {
            // Export placeholders, never the stored secret values.
            var servers = store.McpServerConfigs.ToDictionary(c => c.Name, c => new { command = c.Command, args = c.ArgumentList ?? CommandLineWords.Parse(c.Arguments), type = c.Transport, env = c.EnvironmentVariables.ToDictionary(p => p.Key, p => p.Value.StartsWith("${") ? p.Value : "${" + p.Key + "}"), headers = c.Headers.ToDictionary(p => p.Key, p => p.Value.StartsWith("${") ? p.Value : "<REDACTED>") });
            await ManagementUi.WriteNewJsonAsync(ManagementUi.Require(words, 1, "输出路径"), new { mcpServers = servers }); ManagementUi.Success(ui, "MCP 配置已脱敏导出。"); return;
        }
        var original = Find(ManagementUi.Require(words, 1, "服务名称")); var config = ManagementUi.Clone(original);
        if (action is "prompts" or "prompt" or "resources" or "read" && !service.IsConnected(config.Id) && !await service.ConnectServerAsync(config, ct)) throw new InvalidOperationException(service.GetError(config.Id));
        switch (action)
        {
            case "auth": config.AuthenticationProviderId = ManagementUi.Require(words, 2, "认证 ID（none 清除）") is var auth && auth != "none" ? auth : null; await service.DisconnectServerAsync(config.Id); store.UpdateMcpServerConfig(config); ManagementUi.Success(ui, "认证绑定已保存；connect 重新连接。"); return;
            case "prompts":
                var prompts = await service.ListPromptsAsync(config.Id, ct); ManagementUi.Output(ui, prompts, () => TerminalOutput.Write(new Text(JsonSerializer.Serialize(prompts, new JsonSerializerOptions { WriteIndented = true }) + "\n"))); return;
            case "resources":
                var resources = await service.ListResourcesAsync(config.Id, ct); ManagementUi.Output(ui, resources, () => TerminalOutput.Write(new Text(JsonSerializer.Serialize(resources, new JsonSerializerOptions { WriteIndented = true }) + "\n"))); return;
            case "read":
                var resource = await service.ReadResourceAsync(config.Id, ManagementUi.Require(words, 2, "资源 URI"), ct); ManagementUi.Output(ui, resource, () => TerminalOutput.Write(new Panel(new Text(resource.ToJson())).Header("MCP 资源"))); return;
            case "prompt":
                var args = JsonSerializer.Deserialize<Dictionary<string,object?>>(words.ElementAtOrDefault(3) ?? "{}")!;
                var prompt = await service.GetPromptAsync(config.Id, ManagementUi.Require(words, 2, "模板名称"), args, ct);
                if (!prompt.Ok) throw new InvalidOperationException(prompt.Error);
                using (var json = JsonDocument.Parse(prompt.ToJson())) PendingPrompt = string.Join("\n", json.RootElement.GetProperty("data").GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString()));
                ManagementUi.Output(ui, prompt, () => TerminalOutput.Write(new Panel(new Text(PendingPrompt)).Header("已加载 MCP 提示模板"))); return;
            case "edit":
                config = await WizardAsync(config); Validate(config);
                await service.DisconnectServerAsync(config.Id); store.UpdateMcpServerConfig(config); ManagementUi.Success(ui, "配置已修改并断开旧连接；connect 重新连接。"); return;
            case "connect": case "reconnect": case "enable":
                if (action == "enable") { config.IsEnabled = true; store.UpdateMcpServerConfig(config); }
                if (!await service.ConnectServerAsync(config, ct)) throw new InvalidOperationException(service.GetError(config.Id) ?? "连接失败。");
                ManagementUi.Success(ui, "已连接 " + config.Name); return;
            case "disable": config.IsEnabled = false; store.UpdateMcpServerConfig(config); goto case "disconnect";
            case "disconnect": await service.DisconnectServerAsync(config.Id); ManagementUi.Success(ui, "已断开 " + config.Name); return;
            case "remove":
                if (!await ManagementUi.Confirm(ui, "删除 MCP 服务 " + config.Name + "？")) throw new OperationCanceledException();
                await service.DisconnectServerAsync(config.Id); store.DeleteMcpServerConfig(config.Id); ManagementUi.Success(ui, "服务已删除。"); return;
            case "show": ManagementUi.Output(ui, Safe(config), () => TerminalOutput.Write(new Text(JsonSerializer.Serialize(Safe(config), new JsonSerializerOptions { WriteIndented = true }) + "\n"))); return;
            case "tools":
                if (!service.IsConnected(config.Id) && !await service.ConnectServerAsync(config, ct)) throw new InvalidOperationException(service.GetError(config.Id));
                var tools = service.GetAllServerTools().Single(s => s.ServerId == config.Id).Tools;
                ManagementUi.Output(ui, tools, () => { foreach (var t in tools) TerminalOutput.Write(new Panel(new Text(t.Description + "\n" + t.InputSchemaJson)).Header(Markup.Escape(t.Name))); }); return;
            default: throw new ArgumentException("MCP 操作：list / add / edit / connect / disconnect / tools / enable / disable / remove / import / export。");
        }
    }
    private static void Validate(McpServerConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Command) || c.Name.Length > 100) throw new ArgumentException("MCP 名称和命令不能为空。");
        if (!new[] { "auto", "stdio", "http", "sse" }.Contains(c.Transport)) throw new ArgumentException("传输必须为 stdio / http / sse / auto。");
        if (c.Transport is "http" or "sse" && (!Uri.TryCreate(c.Command, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException("HTTP 服务需要 HTTP(S) 地址。");
        if (c.ArgumentList == null) c.ArgumentList = CommandLineWords.Parse(c.Arguments);
        if (c.ArgumentList.Any(a => a.Contains('\0')) || c.ArgumentList.Length > 100) throw new ArgumentException("MCP 参数无效。");
    }
    private async Task<McpServerConfig> WizardAsync(McpServerConfig c)
    {
        ManagementUi.Interactive(ui);
        c.Name = await ManagementUi.Text("服务名称", c.Name);
        c.Transport = ManagementUi.Choose("传输方式", "stdio", "http", "sse");
        c.Command = await ManagementUi.Text(c.Transport == "stdio" ? "服务可执行文件（不经过 Shell 拼接）" : "MCP Endpoint URL", c.Command);
        c.ArgumentList = c.Transport == "stdio" ? JsonSerializer.Deserialize<string[]>(await ManagementUi.Text("参数 JSON 数组", JsonSerializer.Serialize(c.ArgumentList ?? CommandLineWords.Parse(c.Arguments))))! : Array.Empty<string>();
        var env = await ManagementUi.Text("环境变量 JSON（留空保留；支持 ${ENV_NAME}）", secret: true, empty: true);
        if (env.Length > 0) c.EnvironmentVariables = JsonSerializer.Deserialize<Dictionary<string,string>>(env)!;
        var headers = await ManagementUi.Text("HTTP Headers JSON（留空保留；支持 ${ENV_NAME}）", secret: true, empty: true);
        if (headers.Length > 0) c.Headers = JsonSerializer.Deserialize<Dictionary<string,string>>(headers)!;
        var authenticationId = await ManagementUi.Text("OAuth / 认证插件 ID（留空清除）", c.AuthenticationProviderId, empty: true); c.AuthenticationProviderId = authenticationId.Length > 0 ? authenticationId : null;
        c.TimeoutSeconds = int.Parse(await ManagementUi.Text("连接超时秒数（1–120）", c.TimeoutSeconds.ToString()));
        if (c.TimeoutSeconds is < 1 or > 120) throw new ArgumentException("连接超时范围 1–120 秒。");
        return c;
    }
    private void Import(string path)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new ArgumentException("配置文件超过 1 MB。");
        var node = JsonNode.Parse(File.ReadAllText(path))?["mcpServers"]?.AsObject() ?? throw new ArgumentException("配置需要 mcpServers 对象。");
        var configs = new List<McpServerConfig>();
        foreach (var entry in node)
        {
            var data = entry.Value!;
            var c = new McpServerConfig { Name = entry.Key, Command = data["url"]?.GetValue<string>() ?? data["command"]?.GetValue<string>() ?? "", ArgumentList = data["args"]?.Deserialize<string[]>() ?? Array.Empty<string>(), EnvironmentVariables = data["env"]?.Deserialize<Dictionary<string,string>>() ?? new(), Headers = data["headers"]?.Deserialize<Dictionary<string,string>>() ?? new(), Transport = data["type"]?.GetValue<string>() ?? "auto", IsEnabled = false };
            Validate(c);
            if (store.McpServerConfigs.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("服务已存在：" + c.Name);
            configs.Add(c);
        }
        foreach (var c in configs) store.AddMcpServerConfig(c);
    }
}

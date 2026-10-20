using AllAgent.NET.Core.Localization;

namespace AllAgent.NET.Terminal;

internal sealed record CommandHint(string Text, string Description, string Usage = "", bool Advanced = false, bool IsSkill = false, string Aliases = "")
{
    public string MarkerMarkup => !Text.StartsWith('/') ? "" : IsSkill ? "[blue]● " + HostText.Pick("技能", "skill") + "[/] " : "[green]● " + HostText.Pick("命令", "command") + "[/] ";
}

internal static class CommandCatalog
{
    private static readonly string[] CommonCommands = ["/help", "/model", "/login", "/new", "/workspace", "/web-ui", "/session", "/todo", "/tasks", "/permissions", "/sandbox", "/questions", "/cache", "/undo", "/settings"];
    private static UiLanguage _cachedLanguage;
    private static IReadOnlyList<CommandHint>? _cached;

    public static IReadOnlyList<CommandHint> Common => Builtins.Where(c => !c.Advanced).ToArray();

    public static IReadOnlyList<CommandHint> Builtins
    {
        get
        {
            var language = HostText.Language;
            if (_cached == null || _cachedLanguage != language)
            {
                _cached = Build();
                _cachedLanguage = language;
            }
            return _cached;
        }
    }

    private static IReadOnlyList<CommandHint> Build() => All
        .Select(c => c with { Advanced = !CommonCommands.Contains(c.Text) })
        .OrderBy(c => c.Advanced ? CommonCommands.Length : Array.IndexOf(CommonCommands, c.Text))
        .ToArray();

    private static CommandHint H(string text, string zh, string en, string usageZh = "", string usageEn = "", string aliases = "") =>
        new(text, HostText.Pick(zh, en), usageZh.Length == 0 && usageEn.Length == 0 ? "" : HostText.Pick(usageZh, usageEn), Aliases: aliases);

    private static IEnumerable<CommandHint> All => new CommandHint[]
    {
        H("/powershell", "打开 PowerShell 能力商店面板", "Open the PowerShell capability store", "[panel|list|search 关键词|install 模块 [版本]|commands [模块]]", "[panel|list|search query|install module [version]|commands [module]]"),
        H("/todo", "查看和管理当前会话的 TODO 清单", "View and manage this conversation's TODO list", "[list|add 内容|start 序号|done 序号|pending 序号|remove 序号|clear]", "[list|add text|start index|done index|pending index|remove index|clear]"),
        H("/sandbox", "查看或切换沙箱 · 保存到当前工作区", "View or switch the sandbox · saved to this workspace", "[status|auto|hyperlight|monty]", "[status|auto|hyperlight|monty]"),
        H("/sandbox auto", "优先 Hyperlight，启动失败自动使用 Monty", "Prefer Hyperlight and fall back to Monty", "", ""),
        H("/sandbox hyperlight", "指定 Hyperlight 微虚拟机", "Use the Hyperlight microVM", "", ""),
        H("/sandbox monty", "指定 Monty · 无需虚拟化", "Use Monty · no virtualization required", "", ""),
        H("/sandbox status", "查看沙箱选择和实际后端", "Show the sandbox choice and effective backend", "", ""),
        H("/questions", "设置 Agent 提问偏好 · 默认不追加提示词", "Set the agent's question preference · default adds no prompt", "[default|minimal|balanced|proactive|status]（0–3）", "[default|minimal|balanced|proactive|status] (0-3)"),
        H("/questions default", "默认 · 不追加提示词", "Default · add no prompt"),
        H("/questions minimal", "少问 · 优先自主推进", "Minimal · prefer autonomous progress"),
        H("/questions balanced", "适中 · 澄清关键歧义", "Balanced · clarify key ambiguity"),
        H("/questions proactive", "多问 · 主动确认目标与偏好", "Proactive · confirm goals and preferences"),
        H("/questions status", "查看当前提问偏好", "Show the current question preference"),
        H("/cache", "提示词缓存策略 · TTL 与保留 · 全局设置", "Prompt cache policy · TTL and retention · global", "[status|ttl default|1h|retention default|24h]", "[status|ttl default|1h|retention default|24h]"),
        H("/cache status", "查看当前缓存策略", "Show the current cache policy"),
        H("/cache ttl 1h", "Anthropic 缓存延长到 1 小时（写入 2x 计费）", "Extend Anthropic cache to 1 hour (2x write cost)"),
        H("/cache ttl default", "恢复提供商默认 TTL（Anthropic 5 分钟）", "Restore the provider default TTL (Anthropic 5 minutes)"),
        H("/cache retention 24h", "OpenAI 官方端点显式 24h 保留 · 仅支持机型", "Explicit 24h retention for the official OpenAI endpoint · supported models only"),
        H("/agents", "交互配置子代理：模型、权限、预算与失败策略", "Configure child agents: model, permission, budget and failure policy", "[panel|edit 名称|add|list|show 名称|import 路径.json|remove 名称]", "[panel|edit name|add|list|show name|import path.json|remove name]"),
        H("/queue", "管理已保存的待处理输入", "Manage saved pending inputs", "[add 任务|list|steer|queue|edit|delete ID|run]", "[add task|list|steer|queue|edit|delete ID|run]"),
        H("/timeline", "按用户消息定位历史并执行消息操作", "Locate history by user message and act on messages", "[list|jump|copy|restore|fork|revert ID]", "[list|jump|copy|restore|fork|revert ID]"),
        H("/fork", "选择完整会话或历史消息之前分支", "Branch from the whole conversation or an earlier message", "[--before 消息ID [标题]]", "[--before message-id [title]]"),
        H("/steer", "在模型步骤边界更新当前任务", "Update the running task at the next model step", "任务说明", "task instructions"),
        H("/parent", "返回父会话", "Return to the parent conversation"),
        H("/children", "查看或进入当前会话的子代理", "View or enter this conversation's child agents", "[子会话 ID]", "[child conversation ID]"),
        H("/copy", "复制会话 Markdown", "Copy the conversation as Markdown", "[last 最后回复|id 会话ID]", "[last|id conversation-id]"),
        H("/permissions directories", "查看会话记住的外部目录", "View external directories remembered by this conversation", "[clear 撤销全部]", "[clear]"),
        H("/context-size", "设置当前模型上下文容量（不带参数选择预设）", "Set the current model context capacity (no argument opens presets)", "[auto|32k|64k|128k|200k|256k|400k|500k|1M|1050k|自定义数值]", "[auto|32k|64k|128k|200k|256k|400k|500k|1M|1050k|custom]"),
        H("/background", "启动后台任务并继续聊天", "Start a background task and keep chatting", "任务描述", "task description"),
        H("/subagents", "设置子代理默认模型；默认跟随当前模型", "Set the default child-agent model; follows the current model by default", "[inherit|配置名 [模型 ID]]", "[inherit|config-name [model ID]]"),
        H("/tasks", "子代理和后台任务：结果、授权与取消", "Child agents and background tasks: results, approvals and cancellation"),
        H("/tasks show", "查看任务结果", "Show the task result", "ID"),
        H("/tasks approve", "处理任务授权", "Handle a task approval", "ID"),
        H("/tasks answer", "回答任务问题", "Answer a task question", "ID"),
        H("/tasks cancel", "取消任务", "Cancel the task", "ID"),
        H("/plugins", ".NET 插件管理 / NuGet 商店", ".NET plugin manager / NuGet store"),
        H("/plugins search", "搜索 AllAgent.NET / LanAgent 标签包", "Search AllAgent.NET / LanAgent tagged packages", "关键词", "query"),
        H("/plugins install", "安装并启用 NuGet 插件", "Install and enable a NuGet plugin", "包ID 版本", "package-id version"),
        H("/plugins import", "安装本地插件包", "Install a local plugin package", "路径.nupkg", "path.nupkg"),
        H("/plugins enable", "启用插件", "Enable the plugin", "包ID", "package-id"),
        H("/plugins disable", "禁用插件", "Disable the plugin", "包ID", "package-id"),
        H("/plugins remove", "卸载插件", "Remove the plugin", "包ID", "package-id"),
        H("/ext", "执行插件命令", "Run a plugin command", "名称 参数", "name arguments"),
        H("/hooks", "生命周期 Hooks", "Lifecycle hooks"),
        H("/hooks enable", "启用 Hook", "Enable a hook", "名称", "name"),
        H("/hooks disable", "禁用 Hook", "Disable a hook", "名称", "name"),
        H("/login", "账号登录 · 内置提供商与插件", "Sign in · built-in providers and plugins", "[提供商]", "[provider]"),
        H("/login status", "查看账号与模型连接状态", "Show account and model connection status"),
        H("/logout", "退出账号登录", "Sign out", "[提供商]", "[provider]"),
        H("/help", "快速上手与常用操作", "Quick start and common actions", "[all 查看全部命令]", "[all]"),
        H("/skills", "技能管理面板", "Skill manager"),
        H("/skills store", "在线技能商店", "Online skill store", "[关键词]", "[query]"),
        H("/skills search", "搜索在线技能", "Search online skills", "关键词", "query"),
        H("/skills list", "列出指令技能", "List instruction skills"),
        H("/skills add", "创建技能", "Create a skill"),
        H("/skills import", "导入 SKILL.md 和支持文件", "Import SKILL.md and supporting files", "路径 [project|user]", "path [project|user]"),
        H("/skills show", "查看技能正文", "Show the skill instructions", "名称", "name"),
        H("/skills enable", "启用技能", "Enable the skill", "名称", "name"),
        H("/skills disable", "禁用技能", "Disable the skill", "名称", "name"),
        H("/skills remove", "卸载技能入口", "Remove the skill entry", "名称", "name"),
        H("/skills export", "导出技能包", "Export the skill package", "名称 目录", "name directory"),
        H("/mcp", "MCP 连接管理面板", "MCP connection manager"),
        H("/mcp store", "官方 MCP 商店", "Official MCP store", "[关键词]", "[query]"),
        H("/mcp search", "搜索官方 MCP 目录", "Search the official MCP catalog", "关键词", "query"),
        H("/mcp list", "连接状态与诊断", "Connection status and diagnostics"),
        H("/mcp add", "添加 stdio / HTTP 服务", "Add a stdio / HTTP server"),
        H("/mcp edit", "编辑服务连接", "Edit the server connection", "名称", "name"),
        H("/mcp connect", "连接或重新连接", "Connect or reconnect", "名称", "name"),
        H("/mcp disconnect", "断开连接", "Disconnect", "名称", "name"),
        H("/mcp tools", "查看服务工具和 JSON Schema", "Show server tools and JSON schemas", "名称", "name"),
        H("/mcp enable", "启用服务", "Enable the server", "名称", "name"),
        H("/mcp disable", "禁用并断开", "Disable and disconnect", "名称", "name"),
        H("/mcp remove", "移除服务", "Remove the server", "名称", "name"),
        H("/mcp import", "导入 mcpServers 配置", "Import an mcpServers configuration", "路径", "path"),
        H("/mcp export", "导出脱敏配置", "Export a redacted configuration", "路径", "path"),
        H("/mcp prompts", "列出服务提示模板", "List server prompt templates", "名称", "name"),
        H("/mcp prompt", "加载提示模板并发起任务", "Load a prompt template and start a task", "名称 模板 [参数JSON]", "name template [arguments JSON]"),
        H("/mcp resources", "列出资源", "List resources", "名称", "name"),
        H("/mcp read", "读取文本资源", "Read a text resource", "名称 URI", "name URI"),
        H("/providers", "提供商配置管理", "Provider configuration manager", aliases: "/config"),
        H("/providers edit", "编辑地址、Key、协议和参数", "Edit endpoint, key, protocol and parameters", "名称", "name"),
        H("/providers refresh", "自动获取模型列表", "Fetch the model list", "名称", "name"),
        H("/providers models", "手动编辑模型列表", "Edit the model list manually", "名称", "name"),
        H("/providers show", "查看脱敏配置", "Show the redacted configuration", "名称", "name"),
        H("/providers enable", "启用提供商", "Enable the provider", "名称", "name"),
        H("/providers disable", "禁用提供商", "Disable the provider", "名称", "name"),
        H("/providers remove", "删除提供商", "Remove the provider", "名称", "name"),
        H("/model", "切换提供商与模型", "Switch provider and model", "[配置名] [模型 ID]", "[config-name] [model ID]"),
        H("/models", "查看可用模型", "Show available models"),
        H("/effort", "设置思考强度或预算", "Set reasoning effort or budget", "[级别] [预算]", "[level] [budget]"),
        H("/new", "开始新任务，旧会话自动保留", "Start a new task; the old conversation is kept", aliases: "/clear"),
        H("/session", "选择或恢复会话 · F3", "Select or resume a conversation · F3", "[ID]", aliases: "/sessions /resume"),
        H("/exit", "退出聊天 · 空输入 Ctrl+C 两次或 Ctrl+D 同样可用", "Exit · empty input Ctrl+C twice or Ctrl+D also works"),
        H("/quit", "同 /exit", "Same as /exit"),
        H("/workspace", "查看或切换当前工作目录", "View or switch the current workspace", "[文件夹路径]", "[folder path]"),
        H("/rename", "重命名当前会话", "Rename the current conversation", "标题", "title"),
        H("/branch", "复制当前会话并切换", "Copy the current conversation and switch to it", "[标题]", "[title]"),
        H("/history", "查看当前对话", "View the current conversation"),
        H("/export", "导出 Markdown 或 JSON", "Export Markdown or JSON", "[路径.md|路径.json] [--no-thinking] [--no-tools]", "[path.md|path.json] [--no-thinking] [--no-tools]"),
        H("/context", "上下文占用与消息统计", "Context usage and message statistics"),
        H("/stats", "按模型查看累计输入、输出和总 Token 用量", "Cumulative input, output and total tokens by model"),
        H("/compact", "模型总结较早上下文，完整历史保留", "Summarize older context with the model; full history is kept", "[重点]", "[focus]"),
        H("/checkpoints", "查看任务检查点", "View task checkpoints"),
        H("/diff", "查看文件工具修改预览", "View the file-tool change preview", "[检查点 ID]", "[checkpoint ID]"),
        H("/undo", "撤销上一轮对话和文件修改", "Undo the last turn and file changes", aliases: "/rewind"),
        H("/redo", "重做已撤销任务", "Redo the undone turn"),
        H("/status", "模型、工作区、权限与连接状态", "Model, workspace, permission and connection status"),
        H("/permissions", "切换权限模式", "Switch permission mode", "[restricted|ask|full|plan|simple]", "[restricted|ask|full|plan|simple]"),
        H("/permissions restricted", "限制模式 · 工作区内免审 · 工作区外授权", "Restricted · workspace operations free · outside requires approval"),
        H("/permissions full", "完全访问 · 不逐次审核", "Full access · no per-call approval"),
        H("/permissions plan", "计划模式 · 只读调研 → 计划审批 → 自动执行", "Plan mode · read-only research → approval → automatic execution"),
        H("/permissions simple", "简单模式 · 仅 PowerShell、读文件、改文件", "Simple mode · PowerShell, read file, edit file only"),
        H("/plan", "切换计划模式 · 生成计划并审批后自动执行", "Toggle plan mode · approve a plan, then execute automatically"),
        H("/simple", "切换简单模式 · 只保留三个工具", "Toggle simple mode · keep only three tools"),
        H("/tools", "查看当前 C# 工具契约", "Show the current C# tool contracts"),
        H("/doctor", "检查随包 PowerShell worker、ripgrep 与执行环境", "Check the bundled PowerShell worker, ripgrep and the execution environment", "[install 修复环境]", "[install]"),
        H("/update", "检查并安装更新 · NuGet / GitCode / GitHub", "Check and install updates · NuGet / GitCode / GitHub", "[--check|--source auto|gitcode|github|nuget|--pre|--rollback|--yes]", "[--check|--source auto|gitcode|github|nuget|--pre|--rollback|--yes]"),
        H("/browser", "浏览器操作 · 默认开启 · 外置连接向导", "Browser use · enabled by default · external connection guide", "[on|off|status|connect|tutorial|mode builtin|chromium|cdp URL|executable PATH]", "[on|off|status|connect|tutorial|mode builtin|chromium|cdp URL|executable PATH]"),
        H("/web-ui", "在浏览器中打开 Web 工作台 · 与终端共享工作区与权限", "Open the Web workspace in a browser · shares workspace and permissions with the terminal", "[--lan|--no-lan|--host 0.0.0.0] [--port 端口] [--password 密码] [qr|status|stop]", "[--lan|--no-lan|--host 0.0.0.0] [--port port] [--password password] [qr|status|stop]"),
        H("/web-ui --lan", "监听 0.0.0.0，生成二维码供其他设备访问 · 本机始终免密", "Listen on 0.0.0.0 and show a QR code for other devices · this machine stays password-free"),
        H("/web-ui --no-lan", "关闭局域网监听，仅本机 127.0.0.1 访问 · 与 --local 相同", "Disable LAN listening and use 127.0.0.1 only · same as --local"),
        H("/web-ui --password", "设置局域网访问密码（可选，建议设置）", "Set the optional LAN access password (recommended)", "密码", "password"),
        H("/web-ui qr", "显示局域网访问二维码与地址", "Show the LAN QR code and addresses"),
        H("/web-ui status", "查看 Web 工作台地址与浏览器连接数", "Show the Web workspace URL and connected browsers"),
        H("/web-ui stop", "停止 Web 工作台并断开浏览器", "Stop the Web workspace and disconnect browsers"),
        H("/computer", "电脑操作状态与独立环境（始终可用）", "Computer-use status and isolated environment (always available)", "[status|isolated URL Token文件]", "[status|isolated URL token-file]"),
        H("/memory", "查看或编辑项目指令", "View or edit project instructions", "[show|set 内容]", "[show|set content]"),
        H("/init", "创建项目指令模板", "Create a project-instructions template"),
        H("/settings", "显示、语言与主题设置", "Display, language and theme settings", "[verbose on|off|language auto|zh|en]", "[verbose on|off|language auto|zh|en]"),
        H("/themes", "切换并保存界面主题", "Switch and save the interface theme", "[night|warm|light]", "[night|warm|light]"),
        H("/spinner", "切换并保存加载指示，默认 Mindblown", "Switch and save the loading indicator; Mindblown by default", "[list|样式名称]", "[list|style-name]"),
        H("/thinking", "显示/隐藏思考内容", "Show/hide reasoning content"),
        H("/details", "显示/隐藏工具详情", "Show/hide tool details"),
        H("/language", "切换界面语言", "Switch the interface language", "[auto|zh|en]", "[auto|zh|en]"),
        H("/mcp auth", "给 MCP 绑定认证入口", "Bind an authentication entry to MCP", "服务 ID|none", "server ID|none"),
        H("/providers auth", "给模型绑定认证入口", "Bind an authentication entry to the model", "配置 ID|none", "config ID|none")
    };
}

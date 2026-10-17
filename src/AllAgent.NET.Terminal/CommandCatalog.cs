namespace AllAgent.NET.Terminal;

internal sealed record CommandHint(string Text, string Description, string Usage = "", bool Advanced = false, bool IsSkill = false)
{
    public string MarkerMarkup => !Text.StartsWith('/') ? "" : IsSkill ? "[blue]● 技能[/] " : "[green]● 命令[/] ";
}

internal static class CommandCatalog
{
    private static readonly string[] CommonCommands = ["/help", "/model", "/login", "/new", "/workspace", "/web-ui", "/session", "/todo", "/tasks", "/permissions", "/sandbox", "/questions", "/cache", "/undo", "/settings"];

    public static IReadOnlyList<CommandHint> Common => Builtins.Where(c => !c.Advanced).ToArray();
    public static IReadOnlyList<CommandHint> Builtins { get; } = Build();

    private static IReadOnlyList<CommandHint> Build() => All
        .Select(c => c with { Advanced = !CommonCommands.Contains(c.Text) })
        .OrderBy(c => c.Advanced ? CommonCommands.Length : Array.IndexOf(CommonCommands, c.Text))
        .ToArray();

    private static IEnumerable<CommandHint> All => new CommandHint[]
    {
        new("/powershell", "打开 PowerShell 能力商店面板", "[panel|list|search 关键词|install 模块 [版本]|commands [模块]]"),
        new("/todo", "查看和管理当前会话的 TODO 清单", "[list|add 内容|start 序号|done 序号|pending 序号|remove 序号|clear]"),
        new("/sandbox", "查看或切换沙箱 · 保存到当前工作区", "[status|auto|hyperlight|monty]"),
        new("/sandbox auto", "优先 Hyperlight，启动失败自动使用 Monty"),
        new("/sandbox hyperlight", "指定 Hyperlight 微虚拟机"),
        new("/sandbox monty", "指定 Monty · 无需虚拟化"),
        new("/sandbox status", "查看沙箱选择和实际后端"),
        new("/questions", "设置 Agent 提问偏好 · 默认不追加提示词", "[default|minimal|balanced|proactive|status]（0–3）"),
        new("/questions default", "默认 · 不追加提示词"), new("/questions minimal", "少问 · 优先自主推进"),
        new("/questions balanced", "适中 · 澄清关键歧义"), new("/questions proactive", "多问 · 主动确认目标与偏好"),
        new("/questions status", "查看当前提问偏好"),
        new("/cache", "提示词缓存策略 · TTL 与保留 · 全局设置", "[status|ttl default|1h|retention default|24h]"),
        new("/cache status", "查看当前缓存策略"), new("/cache ttl 1h", "Anthropic 缓存延长到 1 小时（写入 2x 计费）"),
        new("/cache ttl default", "恢复提供商默认 TTL（Anthropic 5 分钟）"), new("/cache retention 24h", "OpenAI 官方端点显式 24h 保留 · 仅支持机型"),
        new("/agents", "交互配置子代理：模型、权限、预算与失败策略", "[panel|edit 名称|add|list|show 名称|import 路径.json|remove 名称]"),
        new("/queue", "管理已保存的待处理输入", "[add 任务|list|steer|queue|edit|delete ID|run]"),
        new("/timeline", "按用户消息定位历史并执行消息操作", "[list|jump|copy|restore|fork|revert ID]"),
        new("/fork", "选择完整会话或历史消息之前分支", "[--before 消息ID [标题]]"),
        new("/steer", "在模型步骤边界更新当前任务", "任务说明"),
        new("/parent", "返回父会话"), new("/children", "查看或进入当前会话的子代理", "[子会话 ID]"),
        new("/copy", "复制会话 Markdown", "[last 最后回复|id 会话ID]"),
        new("/permissions directories", "查看会话记住的外部目录", "[clear 撤销全部]"),
        new("/context-size", "设置当前模型上下文容量（不带参数选择预设）", "[auto|32k|64k|128k|200k|256k|400k|500k|1M|1050k|自定义数值]"),
        new("/background", "启动后台任务并继续聊天", "任务描述"), new("/subagents", "设置子代理默认模型；默认跟随当前模型", "[inherit|配置名 [模型 ID]]"), new("/tasks", "子代理和后台任务：结果、授权与取消"), new("/tasks show", "查看任务结果", "ID"), new("/tasks approve", "处理任务授权", "ID"), new("/tasks answer", "回答任务问题", "ID"), new("/tasks cancel", "取消任务", "ID"),
        new("/plugins", ".NET 插件管理 / NuGet 商店"), new("/plugins search", "搜索 AllAgent.NET / LanAgent 标签包", "关键词"), new("/plugins install", "安装并启用 NuGet 插件", "包ID 版本"), new("/plugins import", "安装本地插件包", "路径.nupkg"), new("/plugins enable", "启用插件", "包ID"), new("/plugins disable", "禁用插件", "包ID"), new("/plugins remove", "卸载插件", "包ID"), new("/ext", "执行插件命令", "名称 参数"),
        new("/hooks", "生命周期 Hooks"), new("/hooks enable", "启用 Hook", "名称"), new("/hooks disable", "禁用 Hook", "名称"), new("/login", "账号登录 · 内置提供商与插件", "[提供商]"), new("/login status", "查看账号与模型连接状态"), new("/logout", "退出账号登录", "[提供商]"),
        new("/help", "快速上手与常用操作", "[all 查看全部命令]"), new("/skills", "技能管理面板"),
        new("/skills store", "在线技能商店", "[关键词]"), new("/skills search", "搜索在线技能", "关键词"), new("/skills list", "列出指令技能"), new("/skills add", "创建技能"), new("/skills import", "导入 SKILL.md 和支持文件", "路径 [project|user]"),
        new("/skills show", "查看技能正文", "名称"), new("/skills enable", "启用技能", "名称"), new("/skills disable", "禁用技能", "名称"), new("/skills remove", "卸载技能入口", "名称"), new("/skills export", "导出技能包", "名称 目录"),
        new("/mcp", "MCP 连接管理面板"), new("/mcp store", "官方 MCP 商店", "[关键词]"), new("/mcp search", "搜索官方 MCP 目录", "关键词"), new("/mcp list", "连接状态与诊断"), new("/mcp add", "添加 stdio / HTTP 服务"), new("/mcp edit", "编辑服务连接", "名称"),
        new("/mcp connect", "连接或重新连接", "名称"), new("/mcp disconnect", "断开连接", "名称"), new("/mcp tools", "查看服务工具和 JSON Schema", "名称"),
        new("/mcp enable", "启用服务", "名称"), new("/mcp disable", "禁用并断开", "名称"), new("/mcp remove", "移除服务", "名称"),
        new("/mcp import", "导入 mcpServers 配置", "路径"), new("/mcp export", "导出脱敏配置", "路径"),
        new("/mcp prompts", "列出服务提示模板", "名称"), new("/mcp prompt", "加载提示模板并发起任务", "名称 模板 [参数JSON]"), new("/mcp resources", "列出资源", "名称"), new("/mcp read", "读取文本资源", "名称 URI"),
        new("/providers", "提供商配置管理"), new("/providers edit", "编辑地址、Key、协议和参数", "名称"),
        new("/providers refresh", "自动获取模型列表", "名称"), new("/providers models", "手动编辑模型列表", "名称"), new("/providers show", "查看脱敏配置", "名称"),
        new("/providers enable", "启用提供商", "名称"), new("/providers disable", "禁用提供商", "名称"), new("/providers remove", "删除提供商", "名称"),
        new("/model", "切换提供商与模型", "[配置名] [模型 ID]"), new("/models", "查看可用模型"), new("/effort", "设置思考强度或预算", "[级别] [预算]"),
        new("/new", "开始新任务，旧会话自动保留"), new("/session", "选择或恢复会话 · F3", "[ID]"),
        new("/exit", "退出聊天 · 空输入 Ctrl+C 两次或 Ctrl+D 同样可用"), new("/quit", "同 /exit"),
        new("/workspace", "查看或切换当前工作目录", "[文件夹路径]"),
        new("/rename", "重命名当前会话", "标题"), new("/branch", "复制当前会话并切换", "[标题]"), new("/history", "查看当前对话"), new("/export", "导出 Markdown 或 JSON", "[路径.md|路径.json] [--no-thinking] [--no-tools]"),
        new("/context", "上下文占用与消息统计"), new("/stats", "按模型查看累计输入、输出和总 Token 用量"), new("/compact", "模型总结并归档旧上下文", "[重点]"),
        new("/checkpoints", "查看任务检查点"), new("/diff", "查看文件工具修改预览", "[检查点 ID]"), new("/undo", "撤销上一轮对话和文件修改"), new("/redo", "重做已撤销任务"),
        new("/status", "模型、工作区、权限与连接状态"), new("/permissions", "切换权限模式", "[ask|full|plan]"), new("/permissions full", "完全访问 · 不逐次审核"), new("/plan", "切换只读规划模式"),
        new("/tools", "查看当前 C# 工具契约"), new("/doctor", "检查 Python、库环境、ripgrep 与执行环境", "[install 修复环境]"),
        new("/browser", "浏览器操作 · 默认开启 · 外置连接向导", "[on|off|status|connect|tutorial|mode builtin|chromium|cdp URL|executable PATH]"),
        new("/web-ui", "在浏览器中打开 Web 工作台 · 与终端共享工作区与权限", "[status|stop]"),
        new("/web-ui status", "查看 Web 工作台地址与浏览器连接数"),
        new("/web-ui stop", "停止 Web 工作台并断开浏览器"),
        new("/computer", "电脑操作总开关 · AI 自动选择模式", "[on|off|status|isolated URL Token文件]"),
        new("/memory", "查看或编辑项目指令", "[show|set 内容]"), new("/init", "创建项目指令模板"),
        new("/settings", "显示与主题设置", "[verbose on|off]"),
        new("/themes", "切换并保存界面主题", "[night|warm|light]"), new("/spinner", "切换并保存加载指示，默认 Mindblown", "[list|样式名称]"), new("/thinking", "显示/隐藏思考内容"), new("/details", "显示/隐藏工具详情"), new("/mcp auth", "给 MCP 绑定认证入口", "服务 ID|none"), new("/providers auth", "给模型绑定认证入口", "配置 ID|none")
    };
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

/// <summary>Edits an isolated draft; only the manager commits a validated role.</summary>
internal sealed class AgentRoleEditor(Func<IReadOnlyList<ApiConfig>> configurations,
    Func<string, string[], string>? choose = null,
    Func<string, string?, Task<string>>? text = null,
    Action<string>? error = null, Action<AgentRoleDefinition>? save = null)
{
    private string Choose(string title, params string[] items) => choose?.Invoke(title, items) ?? ManagementUi.Choose(title, items);
    private Task<string> Text(string label, string? value = null) => text?.Invoke(label, value) ?? ManagementUi.Text(label, value, empty: true);
    private void Error(string message) { if (error != null) error(message); else TerminalOutput.Write(new Text(message + "\n", new Style(Color.Red))); }
    private const string Inherit = "继承默认";

    public async Task<AgentRoleDefinition?> EditAsync(AgentRoleDefinition initial)
    {
        var draft = initial;
        while (true)
        {
            var settings = draft.Execution ?? new();
            if (choose == null) ShowSummary(draft, settings);
            var field = Choose($"编辑 {draft.Name} · {draft.Description} · {draft.Provider ?? "默认提供商"} / {draft.Model ?? "默认模型"} · 尚未保存",
                "保存并返回", "取消修改", "提供商与模型", "推理与温度", "角色说明与指令", "工具与技能权限", "上下文来源", "执行预算", "并发与派发", "失败处理");
            try
            {
                switch (field)
                {
                    case "取消修改": return null;
                    case "保存并返回":
                        settings.Validate();
                        AgentRoleCatalog.Validate(JsonSerializer.Serialize(new[] { draft }));
                        if (draft.Provider != null)
                        {
                            var configs = configurations();
                            var selected = configs.FirstOrDefault(c => c.IsEnabled);
                            if (selected == null) throw new ArgumentException("没有启用的提供商，请先使用 /login 配置。");
                            _ = settings.Apply(SubAgentModelSelection.Resolve(selected, configs, draft.Model, draft.Provider));
                        }
                        save?.Invoke(draft);
                        return draft;
                    case "提供商与模型":
                        var model = await PickModelAsync(draft.Provider, draft.Model);
                        draft = draft with { Provider = model.Provider, Model = model.Model };
                        break;
                    case "角色说明与指令":
                        var description = await Text("角色说明（1–120 字符）", draft.Description);
                        var instructions = await Text("系统指令（可粘贴文本，1–32000 字符）", draft.Instructions);
                        draft = draft with { Description = description, Instructions = instructions };
                        break;
                    case "推理与温度":
                        var temperature = await Number<double>("温度 0–2（空白继承）", settings.Temperature);
                        var efforts = new[] { Inherit, "default", "none", "low", "medium", "high", "xhigh", "thinking", "budget" };
                        if (draft.Provider != null)
                        {
                            var configs = configurations();
                            var current = configs.FirstOrDefault(c => c.IsEnabled);
                            if (current != null) efforts = new[] { Inherit }.Concat(ReasoningSettings.Efforts(SubAgentModelSelection.Resolve(current, configs, draft.Model, draft.Provider))).ToArray();
                        }
                        var effort = Choose("推理强度 · 当前 " + (settings.ReasoningEffort ?? Inherit), efforts);
                        var budget = effort == "budget" ? await Number<int>("思考 Token 预算（必填）", settings.ThinkingBudget) : null;
                        var reasoning = settings with { Temperature = temperature, ReasoningEffort = effort == Inherit ? null : effort, ThinkingBudget = budget };
                        reasoning.Validate(); draft = draft with { Execution = reasoning };
                        break;
                    case "工具与技能权限":
                        draft = await PermissionsAsync(draft);
                        break;
                    case "上下文来源":
                        var project = Choose("项目指令 · 当前 " + (settings.IncludeProjectInstructions ? "包含" : "不包含"), "包含", "不包含") == "包含";
                        draft = draft with { Execution = settings with { IncludeProjectInstructions = project } };
                        break;
                    case "执行预算": draft = draft with { Execution = await BudgetAsync(settings) }; break;
                    case "并发与派发":
                        var concurrent = await Number<int>("角色并发数 1–6", settings.MaxConcurrent) ?? 6;
                        var depth = Choose("派发此角色 · 当前 " + (settings.MaxDepth == 0 ? "禁用" : "启用（一层）"), "启用（一层）", "禁用") == "禁用" ? 0 : 1;
                        var concurrency = settings with { MaxConcurrent = concurrent, MaxDepth = depth };
                        concurrency.Validate(); draft = draft with { Execution = concurrency };
                        break;
                    case "失败处理":
                        var policy = Choose("失败策略 · 当前 " + settings.FailurePolicy, "交回主代理", "重试", "切换备用模型");
                        var retries = policy == "重试" ? await Number<int>("最大重试次数 0–20", settings.MaxRetries) ?? 0 : 0;
                        var fallback = policy == "切换备用模型" ? await PickModelAsync(settings.FallbackProvider, settings.FallbackModel) : (Provider: (string?)null, Model: (string?)null);
                        var failure = settings with { FailurePolicy = policy == "重试" ? "retry" : policy == "切换备用模型" ? "fallback" : "return", MaxRetries = retries, FallbackProvider = fallback.Provider, FallbackModel = fallback.Model };
                        failure.Validate(); draft = draft with { Execution = failure };
                        break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException or JsonException)
            { Error(ex.Message); }
        }
    }

    private async Task<(string? Provider, string? Model)> PickModelAsync(string? provider, string? model)
    {
        var configs = configurations().Where(c => c.IsEnabled).ToArray();
        var labels = configs.Select(c => c.DisplayName + " · " + c.Id.ToString()[..8]).ToArray();
        var choice = Choose("提供商 · 当前 " + (provider ?? Inherit), new[] { Inherit }.Concat(labels).ToArray());
        var selected = choice == Inherit ? null : configs[Array.IndexOf(labels, choice)];
        var models = (selected?.Models ?? []).Prepend(selected?.DefaultModel ?? "").Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();
        var picked = Choose("模型 · 当前 " + (model ?? Inherit), new[] { Inherit, "手动输入模型 ID" }.Concat(models).ToArray());
        if (picked == "手动输入模型 ID") picked = (await Text("模型 ID", model)).Trim();
        return (selected?.Id.ToString(), picked == Inherit || picked.Length == 0 ? null : picked);
    }

    private async Task<AgentRoleDefinition> PermissionsAsync(AgentRoleDefinition role)
    {
        var action = Choose("权限设置", "返回", "只读模式", "工具白名单", "技能白名单", "添加权限规则", "删除权限规则");
        if (action == "只读模式")
        {
            if (role.Name is "explore" or "investigate" or "review") { Error("内置调查与审查角色必须保持只读。"); return role; }
            return role with { ReadOnly = Choose("权限 · 当前 " + (role.ReadOnly ? "只读" : "继承宿主"), "只读", "继承宿主") == "只读" };
        }
        if (action is "工具白名单" or "技能白名单")
        {
            var current = action == "工具白名单" ? role.Tools : role.Execution?.Skills;
            var mode = Choose(action + " · 当前 " + (current == null ? "继承" : string.Join(", ", current)), "继承", "全部禁止", "指定允许项");
            var list = mode == "继承" ? null : mode == "全部禁止" ? [] : ParseList(await Text("名称（逗号分隔）", current == null ? "" : string.Join(",", current)));
            return action == "工具白名单" ? role with { Tools = list } : role with { Execution = (role.Execution ?? new()) with { Skills = list } };
        }
        if (action == "添加权限规则")
        {
            var verb = await Text("操作（如 read、write、skill、*）", "*");
            var resource = await Text("资源路径或匹配模式", "*");
            var effect = Choose("规则效果", "ask", "deny", "allow");
            return role with { Permissions = (role.Permissions ?? []).Append(new(verb, resource, effect)).ToArray() };
        }
        if (action == "删除权限规则" && role.Permissions is { Count: > 0 } rules)
        {
            var labels = rules.Select((r, i) => $"{i + 1}. {r.Action} · {r.Resource} · {r.Effect}").ToArray();
            var picked = Choose("删除哪一条（未匹配的操作需要审批）", new[] { "返回" }.Concat(labels).ToArray());
            if (picked != "返回") return role with { Permissions = rules.Where((_, i) => i != Array.IndexOf(labels, picked)).ToArray() };
        }
        return role;
    }

    private async Task<SubAgentExecutionSettings> BudgetAsync(SubAgentExecutionSettings settings)
    {
        var fields = new (string Key, string Label)[] { ("MaxOutputTokens", "单次输出 Token"), ("MaxContextCharacters", "上下文字符数（至少 8000）"), ("MaxToolCalls", "工具调用次数"), ("TimeoutSeconds", "超时秒数（1–86400）"), ("MaxTotalTokens", "累计 Token"), ("MaxCost", "估算费用与单价") };
        var labels = fields.Select(f => f.Label + " · " + (typeof(SubAgentExecutionSettings).GetProperty(f.Key)!.GetValue(settings)?.ToString() ?? Inherit)).ToArray();
        var picked = Choose("执行预算 · 空白继承，费用需同币种单价", new[] { "返回" }.Concat(labels).ToArray());
        if (picked == "返回") return settings;
        var key = fields[Array.IndexOf(labels, picked)].Key;
        if (key == "MaxCost")
        {
            var cost = await Number<decimal>("费用上限（空白关闭；使用保守估算）", settings.MaxCost);
            var input = cost == null ? null : await Number<decimal>("输入每百万 Token 单价（必填）", settings.InputCostPerMillion);
            var output = cost == null ? null : await Number<decimal>("输出每百万 Token 单价（必填）", settings.OutputCostPerMillion);
            var result = settings with { MaxCost = cost, InputCostPerMillion = input, OutputCostPerMillion = output };
            result.Validate(); return result;
        }
        var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
        node[key] = await Number<long>(fields[Array.IndexOf(labels, picked)].Label + "（空白继承）", node[key]?.GetValue<long>());
        var updated = node.Deserialize<SubAgentExecutionSettings>()!;
        updated.Validate(); return updated;
    }

    private async Task<T?> Number<T>(string label, T? current) where T : struct, IParsable<T>
        => ParseNumber<T>(await Text(label + "；输入 inherit 清除", current == null ? null : Convert.ToString(current.Value, CultureInfo.InvariantCulture)));
    internal static T? ParseNumber<T>(string value) where T : struct, IParsable<T>
        => string.IsNullOrWhiteSpace(value) || value.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase) ? null : T.Parse(value.Trim(), CultureInfo.InvariantCulture);
    internal static string[] ParseList(string value) => value.Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();

    private static void ShowSummary(AgentRoleDefinition role, SubAgentExecutionSettings settings)
    {
        var table = new Table().Title("[bold]子代理配置草稿[/]").AddColumn("配置").AddColumn("当前值");
        void Row(string label, string? value) => table.AddRow(Markup.Escape(label), Markup.Escape(value ?? Inherit));
        Row("角色", role.Name + " · " + role.Description);
        Row("提供商 / 模型", (role.Provider ?? Inherit) + " / " + (role.Model ?? Inherit));
        Row("推理 / 温度", (settings.ReasoningEffort ?? Inherit) + " / " + (settings.Temperature?.ToString(CultureInfo.InvariantCulture) ?? Inherit));
        Row("工具 / 技能", (role.Tools == null ? Inherit : string.Join(", ", role.Tools)) + " / " + (settings.Skills == null ? Inherit : string.Join(", ", settings.Skills)));
        Row("权限", (role.ReadOnly ? "只读" : "继承宿主") + $" · {role.Permissions?.Count ?? 0} 条规则");
        Row("上下文", $"项目指令：{(settings.IncludeProjectInstructions ? "包含" : "不包含")}");
        Row("预算", $"总 Token {settings.MaxTotalTokens?.ToString() ?? Inherit} · 费用 {settings.MaxCost?.ToString() ?? "未设置"} · 超时 {settings.TimeoutSeconds?.ToString() ?? Inherit} 秒");
        Row("并发 / 派发", $"{settings.MaxConcurrent} / {(settings.MaxDepth == 0 ? "禁用" : "一层")}");
        Row("失败处理", settings.FailurePolicy + $" · 重试 {settings.MaxRetries} 次 · 备用 {settings.FallbackModel ?? "未设置"}");
        TerminalOutput.Write(table);
    }
}

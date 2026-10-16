using LanAgent.Core.Data;

namespace LanAgent.Terminal;

internal static class QuestionPreference
{
    internal const string SettingKey = "agent:questions";
    internal static readonly string[] Levels = ["default", "minimal", "balanced", "proactive"];

    internal static string Current(DataStore store) => store.GetSetting(SettingKey) is { } value && Levels.Contains(value) ? value : "default";

    internal static string Label(string level) => level switch
    {
        "minimal" => "少问 · 优先自主推进，仅缺少关键资料时提问",
        "balanced" => "适中 · 关键歧义先澄清，常规细节自行决定",
        "proactive" => "多问 · 主动询问目标、偏好及重要方案选择",
        _ => "默认 · 不追加提示词，保持模型原有行为"
    };

    internal static string AppendInstructions(string instructions, string level)
    {
        var preference = level switch
        {
            "minimal" => "Minimize clarification questions. Make reasonable assumptions for routine, reversible decisions and proceed with the task. Ask only when essential missing information prevents meaningful progress or a wrong assumption would have significant consequences.",
            "balanced" => "Ask concise clarification questions when ambiguity about the goal or an important requirement would materially change the outcome. Group related questions together. Decide routine implementation details yourself and continue useful independent work while awaiting answers.",
            "proactive" => "Proactively ask the user concise questions about unclear goals, preferences, constraints, and important alternative approaches before committing to those decisions. Group related questions together, avoid repeating answered questions, and keep progressing on work that does not depend on the answers.",
            _ => ""
        };
        return preference.Length == 0 ? instructions : instructions + "\n\nQuestion preference:\n" + preference +
            " This preference concerns clarification only. Continue to follow all host permission rules and required approvals.";
    }

    internal static void Execute(DataStore store, TerminalUi ui, IReadOnlyList<string> args)
    {
        if (args.Count > 1) throw new ArgumentException("用法 /questions [default|minimal|balanced|proactive|status]（0–3）。");
        var level = args.FirstOrDefault()?.ToLowerInvariant();
        if (level == null && ui.Interactive && !ui.Json)
        {
            var current = Current(store);
            level = ManagementUi.Choose("提问偏好 · 当前 " + Label(current), Levels,
                value => value + " · " + Label(value) + (value == current ? "（当前）" : ""));
        }
        if (level is null or "status")
        {
            var current = Current(store);
            ManagementUi.Output(ui, new { level = current, description = Label(current) },
                () => TerminalOutput.Write(new Spectre.Console.Text("提问偏好：" + Label(current) + "\n用法 /questions default|minimal|balanced|proactive（0–3）\n")));
            return;
        }
        level = level switch { "0" => "default", "1" => "minimal", "2" => "balanced", "3" => "proactive", _ => level };
        if (!Levels.Contains(level)) throw new ArgumentException("提问偏好：default 默认 / minimal 少问 / balanced 适中 / proactive 多问；也可使用 0–3。");
        store.SetSetting(SettingKey, level);
        ManagementUi.Success(ui, "提问偏好已保存：" + Label(level));
    }
}

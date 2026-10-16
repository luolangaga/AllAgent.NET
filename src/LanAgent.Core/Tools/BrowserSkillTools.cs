using System.ComponentModel;
using System.Text.Json;

namespace LanAgent.Core.Tools;

/// <summary>One compact host bridge. Its contract is supplied by the browser-use skill on demand.</summary>
public partial class BrowserSkillTools(BrowserUseTools browser) : IAgentToolSet, IAgentToolPreflight, IAgentToolApprovalPolicy
{
    public ToolResult? ValidateInvocation(string name, JsonElement arguments) => browser.ValidateInvocation(
        ToolArguments.Get(arguments, "operation", "") == "status" ? "browser_status" : "browser", arguments);
    public ToolApprovalPolicy GetApprovalPolicy(string name, JsonElement arguments) => new(
        ToolArguments.Get(arguments, "operation", "") is not ("status" or "state" or "screenshot" or "observe" or "locate"));

    [AgentTool("browser", Risk = ToolRisk.Execute)]
    [Description("C# browser bridge. Load the built-in browser-use skill for the operation contract.")]
    public async Task<ToolResult> ExecuteAsync([Description("Operation from browser-use skill")] string operation,
        [Description("JSON object containing operation arguments")] string arguments_json = "{}", CancellationToken ct = default)
    {
        using var document = JsonDocument.Parse(arguments_json);
        var args = document.RootElement.Clone();
        if (args.ValueKind != JsonValueKind.Object) return ToolResult.Failure("arguments_json must be a JSON object.");
        if (operation is "send_keys" or "select_dropdown" or "switch" || operation == "close" && args.TryGetProperty("tab_id", out _))
        {
            var values = args.EnumerateObject().ToDictionary(p => p.Name == "tab_id" ? "tabId" : p.Name == "keys" ? "text" : p.Name, p => (object?)p.Value.Clone());
            args = JsonSerializer.SerializeToElement(values);
            operation = operation switch { "send_keys" => "keys", "select_dropdown" => "select", "switch" => "switch_tab", _ => "close_tab" };
        }
        if (operation == "go_back") operation = "back";
        if (operation is "locate" or "act" or "fill_fields" or "click_and_wait" or "observe" or "search" or "navigate" or "wait" or "search_page" or "find_elements" or "find_text" or "dropdown_options")
            return await browser.UpstreamAsync(operation, arguments_json, ct);
        var tool = operation switch
        {
            "status" => "browser_status", "open" => "browser_open", "state" => "browser_state", "screenshot" => "browser_screenshot", "close" => "browser_close",
            "new_tab" => "browser_new_tab", "evaluate" => "browser_evaluate", "dialog" => "browser_dialog", _ => "browser_action"
        };
        if (tool == "browser_action")
        {
            var values = args.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            values["action"] = operation; args = JsonSerializer.SerializeToElement(values);
        }
        var schema = browser.Definitions.Single(t => t.Name == tool).Parameters;
        ToolArguments.Validate(args, schema);
        return await browser.InvokeAsync(tool, args, ct);
    }
}

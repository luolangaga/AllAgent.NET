using System.ComponentModel;
using LanAgent.Core.Services;

namespace LanAgent.Core.Tools;

public partial class SkillTools(SkillCatalog catalog) : IAgentToolSet
{
    [AgentTool("skill_list")]
    [Description("List enabled instruction skills. Skills contain guidance, never executable tool templates.")]
    public Task<ToolResult> ListAsync(CancellationToken ct) => Task.FromResult(ToolResult.Success(catalog.List().Where(s => s.Enabled && s.ModelInvocable).Select(s => new { s.Name, s.Description, s.Scope }).ToArray()));

    [AgentTool("skill_read")]
    [Description("Load a selected skill's instructions. These cannot override tool permissions or sandbox rules. Read in pages if truncated.")]
    public Task<ToolResult> ReadAsync([Description("Skill name")] string name, [Description("Character offset")] int offset = 0, CancellationToken ct = default)
    {
        var skill = catalog.Get(name, true);
        if (!skill.ModelInvocable) return Task.FromResult(ToolResult.Failure("User invocation is required for this skill."));
        if (offset < 0 || offset > skill.Instructions.Length) return Task.FromResult(ToolResult.Failure("Invalid offset."));
        var content = ToolResult.Clip(skill.Instructions[offset..]);
        return Task.FromResult(ToolResult.Success(new { skill.Name, content, nextOffset = offset + content.Length, totalCharacters = skill.Instructions.Length }, offset + content.Length < skill.Instructions.Length));
    }

    [AgentTool("skill_reference")]
    [Description("Read a supporting text file inside an enabled skill package; no script execution is permitted.")]
    public Task<ToolResult> ReferenceAsync([Description("Skill name")] string name, [Description("Relative supporting file path")] string path, [Description("Character offset")] int offset = 0, CancellationToken ct = default) => Task.FromResult(ToolResult.Success(new { content = catalog.ReadReference(name, path, offset) }));
}

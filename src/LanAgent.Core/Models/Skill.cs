using System;

namespace LanAgent.Core.Models;

public enum SkillType { Builtin, Shell, Custom }

public class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public string? CommandTemplate { get; set; }
    public SkillType Type { get; set; } = SkillType.Custom;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

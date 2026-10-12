using System;

namespace LanAgent.Core.Models;

public class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Transport { get; set; } = "auto";
    public string[]? ArgumentList { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? AuthenticationProviderId { get; set; }
    public int TimeoutSeconds { get; set; } = 20;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

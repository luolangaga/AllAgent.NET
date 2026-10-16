namespace LanAgent.Core.Models;

public sealed record TodoItem(string Id, string Content, string Status = "pending");

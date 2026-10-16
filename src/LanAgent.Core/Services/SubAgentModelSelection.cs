using System.Text.Json;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services;

/// <summary>Selects a child configuration without mutating the parent's model or credentials.</summary>
public static class SubAgentModelSelection
{
    public static ApiConfig Resolve(ApiConfig current, IEnumerable<ApiConfig> configured, string? model = null, string? provider = null)
    {
        var selected = current;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            provider = provider.Trim();
            var candidates = configured.Where(c => c.IsEnabled &&
                ((Guid.TryParse(provider, out var id) && c.Id == id) ||
                 (provider.Replace("-", "").Length >= 8 && c.Id.ToString("N").StartsWith(provider.Replace("-", ""), StringComparison.OrdinalIgnoreCase)) ||
                 c.DisplayName.Equals(provider, StringComparison.OrdinalIgnoreCase) ||
                 c.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase))).ToArray();
            selected = candidates.Length switch
            {
                1 => candidates[0],
                0 => throw new ArgumentException("Child provider was not found or is disabled: " + provider),
                _ => throw new ArgumentException("Child provider is ambiguous; select its configuration ID: " + provider)
            };
        }
        if (!selected.IsEnabled) throw new ArgumentException("The selected child provider is disabled.");
        var clone = JsonSerializer.Deserialize<ApiConfig>(JsonSerializer.Serialize(selected))!;
        clone.AuthenticationContextId = selected.AuthenticationContextId;
        AI.ReasoningSettings.RememberForModel(clone);
        if (!string.IsNullOrWhiteSpace(model)) clone.DefaultModel = model.Trim();
        if (string.IsNullOrWhiteSpace(clone.DefaultModel)) throw new ArgumentException("Select a child model before delegating.");
        if (clone.DefaultModel.Length > 256) throw new ArgumentException("Child model ID exceeds 256 characters.");
        AI.ReasoningSettings.RestoreForModel(clone);
        return clone;
    }
}

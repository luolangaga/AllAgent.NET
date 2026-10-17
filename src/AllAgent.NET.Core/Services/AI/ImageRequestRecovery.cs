using System.Text.RegularExpressions;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services.AI;

public static class ImageRequestRecovery
{
    public static ImageAttachment? RejectedImage(string error, IReadOnlyList<ChatMessage> messages)
    {
        if (!error.Contains("HTTP 400", StringComparison.OrdinalIgnoreCase) ||
            !error.Contains("unsupported image", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(error, @"messages\[(\d+)\]\.image\[(\d+)\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var message) ||
            !int.TryParse(match.Groups[2].Value, out var image) || message >= messages.Count || image >= messages[message].Images.Count) return null;
        return messages[message].Images[image];
    }

    public static ChatMessage Remove(ChatMessage message, ImageAttachment image) => message.Images.Contains(image)
        ? message with { Images = message.Images.Where(item => item != image).ToArray(),
            Content = message.Content + $"\n[附件 {image.Name} 被图片服务拒绝，已从模型上下文移除；不能声称已看到该图片。]", ProviderStateJson = null }
        : message;
}

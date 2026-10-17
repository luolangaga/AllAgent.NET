using AllAgent.NET.Core.Models;
using SkiaSharp;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ImagePreparationTests
{
    [Fact]
    public async Task LongScreenshotIsDecodedAndResizedWithinInlineLimits()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "long.png");
        using var bitmap = new SKBitmap(200, 8000);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, encoded.ToArray());
        var attachment = await ImageAttachment.ReadAsync(path);
        using var result = SKBitmap.Decode(Convert.FromBase64String(attachment.Base64));
        Assert.Equal(50, result.Width);
        Assert.Equal(2000, result.Height);
        Assert.True(attachment.Base64.Length <= 4_500_000);
        Assert.Equal("image/png", attachment.MediaType);
    }

    [Fact]
    public async Task RecognizableHeaderDoesNotAdmitBrokenImage()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "broken.png");
        await File.WriteAllBytesAsync(path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0 });
        await Assert.ThrowsAsync<ArgumentException>(() => ImageAttachment.ReadAsync(path));
    }
}

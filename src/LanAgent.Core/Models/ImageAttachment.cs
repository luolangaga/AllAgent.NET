using SkiaSharp;

namespace LanAgent.Core.Models;

public sealed record ImageAttachment(string Name, string MediaType, string Base64)
{
    public const int MaxBytes = 4 * 1024 * 1024;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProcessingNote { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DataUrl => "data:" + MediaType + ";base64," + Base64;
    public static ImageAttachment FromBytes(string name, byte[] bytes)
    {
        if (bytes.Length is < 12 or > MaxBytes) throw new ArgumentException("图片必须为 12 字节至 4 MB。");
        var mime = bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? "image/jpeg"
            : System.Text.Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a" ? "image/gif"
            : System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP" ? "image/webp"
            : throw new ArgumentException("不支持的图片格式；请使用 PNG、JPEG、GIF 或 WebP。");
        return new(Path.GetFileName(name), mime, Convert.ToBase64String(bytes));
    }
    public static void Validate(IReadOnlyList<ImageAttachment> images)
    {
        if (images.Count > 8) throw new ArgumentException("每条消息最多 8 张图片。");
        var total = 0;
        foreach (var image in images)
        {
            if (image.Base64.Length > (MaxBytes + 2) / 3 * 4) throw new ArgumentException("图片超过 4 MB。");
            var bytes = Convert.FromBase64String(image.Base64);
            if (FromBytes(image.Name, bytes).MediaType != image.MediaType) throw new ArgumentException("图片内容与类型不符。");
            total = checked(total + bytes.Length);
        }
        if (total > 12 * 1024 * 1024) throw new ArgumentException("每条消息的图片总量不能超过 12 MB。");
    }
    public static async Task<ImageAttachment> ReadAsync(string path, CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new ArgumentException("图片超过 4 MB。");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, ct);
        var attachment = FromBytes(path, bytes);
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream) ?? throw new ArgumentException("图片无法解码；请重新导出为 PNG 或 JPEG。");
        var info = codec.Info;
        if ((long)info.Width * info.Height > 40_000_000)
            throw new ArgumentException("图片超过 4000 万像素；请先裁剪或缩小。");
        using var decoded = new SKBitmap(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new ArgumentException("图片数据损坏或不完整；请重新导出。");
        // Bound both dimensions and base64 payload, as Pi does before sending inline images.
        var scale = Math.Min(1d, 2000d / Math.Max(info.Width, info.Height));
        if (scale == 1 && attachment.Base64.Length <= 4_500_000) return attachment;
        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var resized = decoded.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear))
                ?? throw new ArgumentException("无法缩小图片。");
            using var image = SKImage.FromBitmap(resized);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            if (png.Size <= 3_375_000) return FromBytes(path, png.ToArray()) with {
                ProcessingNote = $"Original {info.Width}x{info.Height}; attached {width}x{height}. Map image coordinates to the original using x*{info.Width}/{width}, y*{info.Height}/{height}."
            };
            if (width == 1 && height == 1) throw new ArgumentException("无法将图片压缩到传输限制以内。");
            width = Math.Max(1, width * 3 / 4); height = Math.Max(1, height * 3 / 4);
        }
    }
}

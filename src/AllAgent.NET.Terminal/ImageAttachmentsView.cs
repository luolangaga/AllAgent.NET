using AllAgent.NET.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

internal static class ImageAttachmentsView
{
    public static IRenderable Render(IReadOnlyList<ImageAttachment> images) => new Panel(new Text(string.Join('\n',
        images.Select(image => image.Name + " · " + image.MediaType + " · " + (image.Base64.Length * 3 / 4 / 1024) + " KB"))))
        .Header("图片附件 · " + images.Count).Border(BoxBorder.Rounded).BorderColor(Color.Cyan1);
}

using System.ComponentModel;
using System.Text;
using LanAgent.Core.Models;

namespace LanAgent.Core.Tools;

public partial class SystemTools
{
    [AgentTool("file_read"), Description("Read text by 1-based line number, or attach a PNG/JPEG/GIF/WebP image. Text returns complete lines within 2000-line/20000-byte budgets and nextOffset for continuation. Oversized single lines support column_offset pagination. External paths require directory approval.")]
    public async Task<ToolResult> ReadAsync([Description("Relative or absolute file path")] string path,
        [Description("Starting line number, 1-based")] int offset = 1,
        [Description("Positive requested line count; large requests are paginated by the output budget")] int limit = ToolOutputWindow.MaxLines,
        CancellationToken ct = default,
        [Description("Character offset within an oversized single line, zero-based; use nextColumnOffset to continue")] int column_offset = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (offset < 1 || limit < 1 || column_offset < 0)
            return ToolResult.Failure("offset is a positive 1-based line number, limit must be positive, and column_offset must be nonnegative.");
        var target = workspace.Resolve(path);
        if (!File.Exists(target)) return ToolResult.Failure("Read path must be an existing file.");
        await using (var probe = File.OpenRead(target))
        {
            var header = new byte[12]; var count = await probe.ReadAtLeastAsync(header, 12, false, ct);
            if (count == 12 && IsImage(header))
            {
                var attachment = await ImageAttachment.ReadAsync(target, ct);
                return ToolResult.Success(new { path, mediaType = attachment.MediaType, image = true, note = "Image attached to the next model request." }) with { Images = [attachment] };
            }
        }
        using var reader = new StreamReader(target, new UTF8Encoding(false, true), true);
        var selected = new StringBuilder(); var lineNumber = 0; var outputLines = 0; var outputBytes = 0; var outputJsonBytes = 0;
        var nextOffset = offset; int? nextColumnOffset = null; string? truncatedBy = null;
        var stopped = false; var firstLineExceedsLimit = false;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            lineNumber++;
            if (line.Contains('\0')) return ToolResult.Failure("File contains NUL bytes; binary text reading is unsupported.");
            if (lineNumber < offset || stopped) continue;
            if (column_offset > 0 && lineNumber == offset)
            {
                if (column_offset > line.Length) return ToolResult.Failure("column_offset is beyond the selected line.");
                if (column_offset < line.Length && char.IsLowSurrogate(line[column_offset])) return ToolResult.Failure("column_offset must be on a Unicode character boundary.");
                line = line[column_offset..];
            }
            var bytes = Encoding.UTF8.GetByteCount(line);
            var jsonBytes = ToolOutputWindow.JsonBytes(line);
            if (outputLines == Math.Min(limit, ToolOutputWindow.MaxLines)) { truncatedBy = "lines"; stopped = true; continue; }
            if (bytes + outputBytes + (outputLines > 0 ? 1 : 0) > ToolOutputWindow.MaxBytes ||
                jsonBytes + outputJsonBytes + (outputLines > 0 ? 2 : 0) > ToolOutputWindow.MaxBytes)
            {
                truncatedBy = "bytes"; stopped = true;
                if (outputLines == 0)
                {
                    firstLineExceedsLimit = true;
                    var fragment = ToolOutputWindow.JsonHead(line);
                    selected.Append(fragment); outputBytes = Encoding.UTF8.GetByteCount(fragment); outputLines = 1;
                    nextColumnOffset = column_offset + fragment.Length; nextOffset = offset;
                }
                continue;
            }
            if (outputLines > 0) selected.Append('\n');
            selected.Append(line); outputBytes += bytes + (outputLines > 0 ? 1 : 0); outputLines++;
            outputJsonBytes += jsonBytes + (outputLines > 1 ? 2 : 0);
            nextOffset = lineNumber + 1;
            if (column_offset > 0) stopped = true;
        }
        if (lineNumber == 0 && offset == 1) return ToolResult.Success(new { path, content = "", offset, nextOffset = 1, totalLines = 0, outputLines = 0, outputBytes = 0 });
        if (offset > lineNumber) return ToolResult.Failure($"offset={offset} is beyond EOF ({lineNumber} lines). Use a line number within the file.");
        var truncated = nextColumnOffset != null || nextOffset <= lineNumber;
        var notice = nextColumnOffset != null ? $"Line {offset} exceeds the byte budget. Continue with offset={offset}, column_offset={nextColumnOffset}." :
            truncated ? $"Continue with offset={nextOffset} (line number)." : "End of file.";
        return ToolResult.Success(new { path, content = selected.ToString(), offset, nextOffset, totalLines = lineNumber, outputLines, outputBytes,
            nextColumnOffset, firstLineExceedsLimit, truncatedBy, notice }, truncated);
    }

    private static bool IsImage(byte[] header) =>
        header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
        header[0] == 255 && header[1] == 216 && header[2] == 255 ||
        Encoding.ASCII.GetString(header, 0, 6) is "GIF87a" or "GIF89a" ||
        Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP";
}

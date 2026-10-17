using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace AllAgent.NET.Tests;

// ConPTY streams contain cursor-addressed updates, not complete lines. Reconstruct
// the screen before asserting editable text; stripping ANSI loses overwrite semantics.
internal static class TerminalScreenCapture
{
    public static string Render(string output, int width = 120, int height = 40)
    {
        var cells = new string?[height, width]; var x = 0; var y = 0; var savedX = 0; var savedY = 0;
        var sequence = new StringBuilder(); var state = 0;
        void Clear(int row, int start, int end) { for (var column = Math.Max(0, start); column < Math.Min(width, end); column++) cells[row, column] = null; }
        void Position() { x = Math.Clamp(x, 0, width - 1); y = Math.Clamp(y, 0, height - 1); }
        foreach (var rune in output.EnumerateRunes())
        {
            var c = rune.Value;
            if (state == 3) { if (c == 7) state = 0; else if (c == 27) state = 4; continue; }
            if (state == 4) { state = c == '\\' ? 0 : 3; continue; }
            if (state == 1)
            {
                state = c == '[' ? 2 : c == ']' ? 3 : 0;
                if (c == '7') { savedX = x; savedY = y; }
                if (c == '8') { x = savedX; y = savedY; }
                sequence.Clear(); continue;
            }
            if (state == 2)
            {
                if (c is < '@' or > '~') { sequence.Append(rune); continue; }
                var parts = sequence.ToString().Split(';');
                int Number(int index, int fallback) => index < parts.Length && int.TryParse(parts[index], out var value) && value > 0 ? value : fallback;
                switch (c)
                {
                    case 'H': case 'f': y = Number(0, 1) - 1; x = Number(1, 1) - 1; break;
                    case 'A': y -= Number(0, 1); break;
                    case 'B': y += Number(0, 1); break;
                    case 'C': x += Number(0, 1); break;
                    case 'D': x -= Number(0, 1); break;
                    case 'G': x = Number(0, 1) - 1; break;
                    case 'd': y = Number(0, 1) - 1; break;
                    case 'J':
                        if (Number(0, 0) is 2 or 3) Array.Clear(cells);
                        else { Clear(y, x, width); for (var row = y + 1; row < height; row++) Clear(row, 0, width); }
                        break;
                    case 'K':
                        var mode = Number(0, 0); Clear(y, mode == 0 ? x : 0, mode == 1 ? x + 1 : width); break;
                }
                Position(); state = 0; continue;
            }
            if (c == 27) { state = 1; continue; }
            if (c == '\r') { x = 0; continue; }
            if (c == '\n')
            {
                if (++y >= height)
                {
                    for (var row = 1; row < height; row++) for (var column = 0; column < width; column++) cells[row - 1, column] = cells[row, column];
                    Clear(height - 1, 0, width); y = height - 1;
                }
                continue;
            }
            if (c == '\b') { x = Math.Max(0, x - 1); continue; }
            if (c < 32) continue;
            var text = rune.ToString(); var size = new Segment(text).CellCount();
            if (size == 0) continue;
            cells[y, x] = text;
            for (var continuation = 1; continuation < size && x + continuation < width; continuation++) cells[y, x + continuation] = "";
            x = Math.Min(width - 1, x + size);
        }
        return string.Join('\n', Enumerable.Range(0, height).Select(row => string.Concat(Enumerable.Range(0, width).Select(column => cells[row, column] ?? " ")).TrimEnd()));
    }
}

public sealed class TerminalScreenCaptureTests
{
    [Fact]
    public void CursorDeltasComposeInputAndOverwritePreviousCells()
    {
        var output = "\u001b[2;1H\u001b[31mS\u001b[0m\u001b[2;1HSE\u001b[2;2HEC\u001b[2;3HCO\u001b[2;4HON\u001b[2;5HND";
        Assert.Contains("SECOND", TerminalScreenCapture.Render(output));
        Assert.Contains("SE", TerminalScreenCapture.Render(output + "\u001b[2;3H\u001b[K"));
        Assert.DoesNotContain("SECOND", TerminalScreenCapture.Render(output + "\u001b[2;3H\u001b[K"));
    }
    [Fact]
    public void WideTextAndOscDoNotShiftAddressedAsciiText()
    {
        Assert.Contains("中 AB", TerminalScreenCapture.Render("\u001b]0;window title\u0007中 \u001b[1;4HAB"));
    }
}

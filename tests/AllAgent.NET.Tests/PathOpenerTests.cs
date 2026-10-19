using System;
using System.IO;
using AllAgent.NET.Core.Runtime;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class PathOpenerTests
{
    [Theory]
    [InlineData("page.html", true)]
    [InlineData("DRAWING.SVG", true)]
    [InlineData("clip.mp4", true)]
    [InlineData("shot.png", true)]
    [InlineData("report.pdf", true)]
    [InlineData("notes.md", true)]
    [InlineData("App.tsx", false)]
    [InlineData("AllAgent.NET.Terminal.csproj", false)]
    [InlineData("installer.exe", false)]
    [InlineData("bundle.zip", false)]
    [InlineData("LICENSE", false)]
    public void PreviewCoversWhatABrowserRendersItself(string name, bool expected) =>
        Assert.Equal(expected, PathOpener.CanPreview(name));

    [Fact]
    public void DefaultBrowserIsWhateverWindowsOpensHttpAddressesWith()
    {
        var browser = PathOpener.DefaultBrowser();
        // The lookup reads the shell's own choice: it either names an installed browser or
        // gives up, never a path that is not there.
        Assert.True(browser == null || File.Exists(browser), $"unexpected browser path: {browser}");
    }

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1", "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe")]
    [InlineData("\"C:\\Program Files\\Firefox\\firefox.exe\" -osint -url \"%1\"", "C:\\Program Files\\Firefox\\firefox.exe")]
    [InlineData("C:\\Program Files\\Chrome\\chrome.exe -- \"%1\"", "C:\\Program Files\\Chrome\\chrome.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExecutableComesOutOfAShellCommandLine(string? command, string? expected) =>
        Assert.Equal(expected, PathOpener.Executable(command));

    [Fact]
    public void MissingPathsFailWithoutStartingAnything() =>
        Assert.False(PathOpener.Open(Path.Combine(Path.GetTempPath(), "allagent-missing-" + Guid.NewGuid().ToString("N") + ".html")));
}

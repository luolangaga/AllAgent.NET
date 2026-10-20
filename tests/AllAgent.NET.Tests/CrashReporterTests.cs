using System.Text.Json;
using AllAgent.NET.Diagnostics;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Crash bundles: self-contained files, redacted secrets, optional dump, and pending state.</summary>
public sealed class CrashReporterTests
{
    [Fact]
    public void ReportWritesBundleAndRedactsSecrets()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "crashes");
        var log = Path.Combine(workspace.Root, "allagent.log");
        File.WriteAllText(log, "Authorization: Bearer abcdefghijklmnop\napi_key=sk-1234567890abcdef\nnormal line\n");
        var options = Options(root, log);

        var report = CrashReporter.Report(
            new InvalidOperationException("boom password=hunter2 sk-1234567890abcdef"),
            "tests", fatal: true, options: options, notify: false);

        Assert.NotNull(report);
        Assert.True(Directory.Exists(report!.Folder));
        Assert.True(File.Exists(report.MarkdownPath));
        Assert.True(File.Exists(report.JsonPath));
        Assert.True(File.Exists(report.UrlPath));
        Assert.Null(report.DumpPath);
        Assert.False(File.Exists(Path.Combine(report.Folder, "memory.dmp")));
        var markdown = File.ReadAllText(report.MarkdownPath);
        Assert.Contains("InvalidOperationException", markdown);
        Assert.Contains("[REDACTED]", markdown);
        Assert.DoesNotContain("hunter2", markdown);
        Assert.DoesNotContain("sk-1234567890abcdef", markdown);
        Assert.DoesNotContain("abcdefghijklmnop", markdown);
        Assert.Contains(report.IssueUrl, File.ReadAllText(report.UrlPath));
        Assert.StartsWith(CrashReporter.DefaultRepository + "/issues/new?", report.IssueUrl);
    }

    [Fact]
    public void ReportJsonCarriesTheSummaryAndPendingStateTracksNotifications()
    {
        using var workspace = new TestWorkspace();
        var options = Options(Path.Combine(workspace.Root, "crashes"), Path.Combine(workspace.Root, "allagent.log"));
        var report = CrashReporter.Report(new InvalidOperationException("pending"), "tests", fatal: true, options: options, notify: false);

        Assert.NotNull(report);
        using (var json = JsonDocument.Parse(File.ReadAllText(report!.JsonPath)))
        {
            Assert.True(json.RootElement.GetProperty("fatal").GetBoolean());
            Assert.Equal(report.Id, json.RootElement.GetProperty("id").GetString());
            Assert.Equal("tests", json.RootElement.GetProperty("component").GetString());
            Assert.Equal("System.InvalidOperationException", json.RootElement.GetProperty("exception").GetProperty("type").GetString());
        }

        Assert.Single(CrashReporter.GetPendingReports(3, options));
        Assert.Single(CrashReporter.ListReports(options: options));
        CrashReporter.MarkNotified(report);
        Assert.Empty(CrashReporter.GetPendingReports(3, options));
        var notified = Assert.Single(CrashReporter.ListReports(options: options));
        Assert.True(notified.Notified);
        Assert.False(notified.Reported);
        CrashReporter.MarkReported(report);
        Assert.True(Assert.Single(CrashReporter.ListReports(options: options)).Reported);
    }

    [Fact]
    public void NonFatalReportsDoNotEnterThePendingPrompt()
    {
        using var workspace = new TestWorkspace();
        var options = Options(Path.Combine(workspace.Root, "crashes"), Path.Combine(workspace.Root, "allagent.log"));
        var report = CrashReporter.Report(new InvalidOperationException("unobserved"), "tests", fatal: false, options: options, notify: false);

        Assert.NotNull(report);
        Assert.Empty(CrashReporter.GetPendingReports(3, options));
        Assert.False(Assert.Single(CrashReporter.ListReports(options: options)).Fatal);
    }

    [Fact]
    public void RetentionKeepsTheConfiguredNumberOfBundles()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "crashes");
        var options = new CrashOptions
        {
            RootDirectory = root,
            LogPath = Path.Combine(workspace.Root, "allagent.log"),
            WriteDump = false,
            MaxBundles = 2,
        };

        for (var index = 0; index < 3; index++)
            Assert.NotNull(CrashReporter.Report(new InvalidOperationException("retention " + index), "tests", fatal: true, options: options, notify: false));

        Assert.Equal(2, Directory.GetDirectories(root).Length);
    }

    [Fact]
    public void GitHubIssueUrlStaysBoundedForALargeStackTrace()
    {
        using var workspace = new TestWorkspace();
        var options = new CrashOptions
        {
            RootDirectory = Path.Combine(workspace.Root, "crashes"),
            LogPath = Path.Combine(workspace.Root, "allagent.log"),
            WriteDump = false,
            MaxIssueUrlLength = 7000,
        };
        var report = CrashReporter.Report(new InvalidOperationException(new string('x', 50000)), "tests", fatal: true, options: options, notify: false);

        Assert.NotNull(report);
        Assert.True(report!.IssueUrl.Length <= 7000, $"Issue URL was {report.IssueUrl.Length} characters.");
        Assert.Contains("issues/new?title=", report.IssueUrl);
        Assert.Contains("truncated", Uri.UnescapeDataString(report.IssueUrl));
        Assert.Contains(new string('x', 1000), File.ReadAllText(report.MarkdownPath));
    }

    private static CrashOptions Options(string root, string log) => new()
    {
        RootDirectory = root,
        LogPath = log,
        WriteDump = false,
    };
}

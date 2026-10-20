using System.Net;
using System.Security.Cryptography;
using System.Text;
using AllAgent.NET.Core.Services.Updates;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void VersionComparisonHandlesStableAndPrerelease()
    {
        Assert.True(UpdateVersion.IsNewer("1.1.0", "1.0.7"));
        Assert.True(UpdateVersion.IsNewer("v1.1.0", "1.0.7"));
        Assert.False(UpdateVersion.IsNewer("1.0.7", "1.0.7"));
        Assert.False(UpdateVersion.IsNewer("1.0.6", "1.0.7"));
        Assert.True(UpdateVersion.IsNewer("1.1.0-beta.2", "1.1.0-beta.1"));
    }

    [Fact]
    public void SettingsRoundTripAndValidation()
    {
        var values = new Dictionary<string, string?>
        {
            ["update:source"] = "github",
            ["update:channel"] = "pre",
            ["update:autoCheck"] = "off",
            ["update:autoInstall"] = "on",
            ["update:requireSignature"] = "1",
            ["update:requireChecksum"] = "0",
        };
        var settings = UpdateSettings.FromSettings(key => values.GetValueOrDefault(key));
        Assert.Equal(UpdateSource.GitHub, settings.Source);
        Assert.Equal(UpdateChannel.Preview, settings.Channel);
        Assert.False(settings.AutoCheck);
        Assert.True(settings.AutoInstall);
        Assert.True(settings.RequireSignature);
        Assert.False(settings.RequireChecksum);
        Assert.Null(UpdateSettings.ParseSource("nope"));
        Assert.Equal(UpdateSource.NuGet, UpdateSettings.ParseSource("NUGET"));
    }

    [Fact]
    public void DetectsNuGetStoreAndPackageId()
    {
        var root = TempDirectory();
        try
        {
            var store = Path.Combine(root, ".dotnet", "tools", ".store", "allagent.net.terminal", "1.0.7",
                "allagent.net.terminal", "1.0.7", "tools", "net10.0", "any");
            Directory.CreateDirectory(store);
            var context = UpdateInstallContext.Detect(store);
            Assert.Equal(UpdateInstallKind.NuGet, context.Kind);
            Assert.Equal("allagent.net.terminal", context.NuGetPackageId);
            Assert.Equal("win-x64", context.Architecture);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void NuGetRestartUsesTheShimInsteadOfTheOldDll()
    {
        var root = TempDirectory();
        try
        {
            var tools = Path.Combine(root, "tools");
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "allagent.cmd"), "@echo off");
            var context = new UpdateInstallContext(UpdateInstallKind.NuGet, root, "win-x64", "x64",
                "AllAgent.NET.Terminal", tools, "dotnet");
            var work = UpdateWorkDirectory.Create("1.1.0");
            var plan = UpdateLauncher.BuildPlan(work, context, UpdateSource.NuGet, "1.1.0", "1.0.7", "", null,
                restart: true, restartArguments: ["web-ui"], workingDirectory: root);
            Assert.EndsWith("allagent.cmd", plan.RestartFileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("web-ui", plan.RestartCommandLine);
            Assert.Contains("--update-health", plan.RestartCommandLineWithHealth);
            Assert.Contains("--update-result", plan.RestartCommandLineWithResult);
            var planPath = UpdateLauncher.WritePlan(work, plan);
            Assert.True(File.Exists(planPath));
            Assert.True(File.Exists(work.ScriptPath));
            Assert.Contains("Wait-Health", File.ReadAllText(work.ScriptPath));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ChecksumVerificationRejectsMismatchAndMissingFile()
    {
        var root = TempDirectory();
        try
        {
            var file = Path.Combine(root, "payload.bin");
            await File.WriteAllTextAsync(file, "hello");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hello")));
            var ok = await UpdateVerification.VerifyChecksumAsync(file, $"{hash} *payload.bin", "payload.bin", true, default);
            Assert.True(ok.Ok);
            Assert.False(ok.Missing);

            var bad = await UpdateVerification.VerifyChecksumAsync(file, $"{new string('0', 64)} *payload.bin", "payload.bin", true, default);
            Assert.False(bad.Ok);
            Assert.False(bad.Missing);

            var missing = await UpdateVerification.VerifyChecksumAsync(file, null, "payload.bin", false, default);
            Assert.True(missing.Ok);
            Assert.True(missing.Missing);

            var required = await UpdateVerification.VerifyChecksumAsync(file, null, "payload.bin", true, default);
            Assert.False(required.Ok);
            Assert.True(required.Missing);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task GitCodeIsPreferredWhenReachable()
    {
        var root = TempDirectory();
        try
        {
            var handler = new StubHandler(request =>
            {
                Assert.Contains("api.gitcode.com", request.RequestUri!.Host);
                return Json(GitCodeRelease("1.1.0"));
            });
            var service = CreateService(root, handler);
            var result = await service.CheckAsync(new UpdateSettings(UpdateSource.Auto), force: true, default);
            Assert.True(result.UpdateAvailable);
            Assert.Equal(UpdateSource.GitCode, result.Source);
            Assert.Equal("1.1.0", result.LatestVersion);
            Assert.NotNull(result.Release);
            Assert.Equal(2, result.Release!.Assets.Count);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task FallsBackToGitHubWhenGitCodeFails()
    {
        var root = TempDirectory();
        try
        {
            var handler = new StubHandler(request =>
            {
                if (request.RequestUri!.Host == "api.gitcode.com")
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);
                Assert.Contains("api.github.com", request.RequestUri.Host);
                return Json(GitHubRelease("1.2.0"));
            });
            var service = CreateService(root, handler);
            var result = await service.CheckAsync(new UpdateSettings(UpdateSource.Auto), force: true, default);
            Assert.True(result.UpdateAvailable);
            Assert.Equal(UpdateSource.GitHub, result.Source);
            Assert.Equal("1.2.0", result.LatestVersion);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task NuGetInstallShapeChecksTheNuGetFeed()
    {
        var root = TempDirectory();
        try
        {
            var handler = new StubHandler(request =>
            {
                Assert.Contains("api.nuget.org", request.RequestUri!.Host);
                return Json("""{"versions":["1.0.7","1.1.0","1.2.0-beta.1"]}""");
            });
            var context = new UpdateInstallContext(UpdateInstallKind.NuGet, root, "win-x64", "x64",
                "AllAgent.NET.Terminal", null, "dotnet");
            var service = new UpdateService(
                new UpdateServiceOptions("1.0.7", root, context),
                new HttpClient(handler));
            var result = await service.CheckAsync(new UpdateSettings(UpdateSource.Auto), force: true, default);
            Assert.True(result.UpdateAvailable);
            Assert.Equal(UpdateSource.NuGet, result.Source);
            Assert.Equal("1.1.0", result.LatestVersion);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void StateStoreRoundTrips()
    {
        var root = TempDirectory();
        try
        {
            var store = new UpdateStateStore(root);
            store.Save(new UpdateState
            {
                LatestVersion = "1.1.0",
                LatestSource = "GitCode",
                ReleaseUrl = "https://gitcode.com/example",
                PreviousVersion = "1.0.7",
                LastResult = "success",
            });
            var loaded = store.Load();
            Assert.Equal("1.1.0", loaded.LatestVersion);
            Assert.Equal("GitCode", loaded.LatestSource);
            Assert.Equal("1.0.7", loaded.PreviousVersion);
            Assert.Equal("success", loaded.LastResult);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static UpdateService CreateService(string stateDirectory, HttpMessageHandler handler) =>
        new(
            new UpdateServiceOptions(
                "1.0.7",
                stateDirectory,
                new UpdateInstallContext(UpdateInstallKind.Installer, stateDirectory, "win-x64", "x64", null, null, "dotnet")),
            new HttpClient(handler));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string GitCodeRelease(string version) => $$"""
    {
      "tag_name": "v{{version}}",
      "name": "AllAgent.NET {{version}}",
      "body": "release notes",
      "prerelease": false,
      "release_status": "latest",
      "assets": [
        { "name": "AllAgent.NET-{{version}}-win-x64-setup.exe", "browser_download_url": "https://example.test/setup-{{version}}.exe" },
        { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt" }
      ]
    }
    """;

    private static string GitHubRelease(string version) => $$"""
    {
      "tag_name": "v{{version}}",
      "name": "AllAgent.NET {{version}}",
      "body": "release notes",
      "prerelease": false,
      "html_url": "https://github.com/luolangaga/AllAgent.NET/releases/tag/v{{version}}",
      "assets": [
        { "name": "AllAgent.NET-{{version}}-win-x64-setup.exe", "browser_download_url": "https://example.test/setup-{{version}}.exe" },
        { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt" }
      ]
    }
    """;

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "allagent-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

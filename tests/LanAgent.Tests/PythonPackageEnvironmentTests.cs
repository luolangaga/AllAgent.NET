using System.Text.Json;
using LanAgent.Core.CodeAct;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class PythonPackageEnvironmentTests
{
    [Theory]
    [InlineData("requests", true)]
    [InlineData("requests==2.32.5", true)]
    [InlineData("httpx[http2]", true)]
    [InlineData("--target=other", false)]
    [InlineData("../package.whl", false)]
    [InlineData("requests; print('x')", false)]
    public void InstallerAcceptsPackageRequirementsWithoutPipOptions(string value, bool expected)
        => Assert.Equal(expected, PythonPackageEnvironment.ValidRequirement(value));

    [Fact]
    public async Task InventoryDocumentsAliasesAndContextComeFromInstalledMetadataWithoutNetwork()
    {
        using var temporary = new TestWorkspace();
        var root = Path.Combine(temporary.Root, "packages");
        var environment = new PythonPackageEnvironment(root);
        var create = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(temporary.Python, ["-m", "venv", "--without-pip", Path.Combine(root, "python", "venv")]), TimeSpan.FromSeconds(60), default);
        Assert.Equal(0, create.ExitCode);
        var site = Path.Combine(root, "python", "venv", "Lib", "site-packages");
        var demo = Path.Combine(site, "demo-1.0.dist-info"); Directory.CreateDirectory(demo);
        File.WriteAllText(Path.Combine(demo, "METADATA"), "Metadata-Version: 2.1\nName: demo\nVersion: 1.0\nProject-URL: Documentation, https://example.test/docs\n\nDemo usage documentation.");
        File.WriteAllText(Path.Combine(demo, "top_level.txt"), "csv\ncall_tool\n");
        File.WriteAllText(Path.Combine(demo, "RECORD"), "demo-1.0.dist-info/METADATA,,\ndemo-1.0.dist-info/top_level.txt,,\ndemo-1.0.dist-info/RECORD,,\n");
        var builtin = Path.Combine(site, "requests-1.0.dist-info"); Directory.CreateDirectory(builtin);
        File.WriteAllText(Path.Combine(builtin, "METADATA"), "Metadata-Version: 2.1\nName: requests\nVersion: 1.0\n\nBuilt-in documentation not needed.");
        var requests = Path.Combine(site, "requests"); Directory.CreateDirectory(requests); File.WriteAllText(Path.Combine(requests, "__init__.py"), "__version__ = '1.0'\n");
        File.WriteAllText(Path.Combine(builtin, "RECORD"), "requests/__init__.py,,\nrequests-1.0.dist-info/METADATA,,\nrequests-1.0.dist-info/RECORD,,\n");
        var incomplete = Path.Combine(site, "numpy-1.0.dist-info"); Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "METADATA"), "Metadata-Version: 2.1\nName: numpy\nVersion: 1.0\n");
        File.WriteAllText(Path.Combine(incomplete, "RECORD"), "numpy/__init__.py,,\nnumpy-1.0.dist-info/METADATA,,\n");
        await environment.RefreshAsync();
        Assert.True(environment.Ready);
        Assert.Contains("requests", File.ReadAllText(environment.CatalogPath));
        Assert.DoesNotContain("numpy", File.ReadAllText(environment.CatalogPath));
        Assert.False(environment.Aliases().ContainsKey("np"));
        Assert.Contains("Demo usage documentation", environment.ReadDocument("demo"));
        Assert.False(Directory.Exists(Path.Combine(root, "python", "docs", "requests")));
        Assert.Equal("csv", environment.Aliases()["csv"]); Assert.False(environment.Aliases().ContainsKey("call_tool"));
        var options = temporary.Options with { FullAccess = true, PythonPath = environment.PythonPath, PythonLibraries = environment };
        await using var runtime = await AgentRuntime.CreateAsync(options);
        Assert.Contains("Installed host libraries (not guest imports; use host tools):\n" + File.ReadAllText(environment.CatalogPath), runtime.CodeAct.Instructions);
        var executableLine = File.ReadLines(environment.CatalogPath).Single(line => line.StartsWith("Host Python executable: ", StringComparison.Ordinal));
        var catalogPython = JsonSerializer.Deserialize<string>(executableLine["Host Python executable: ".Length..]);
        Assert.Equal(Path.GetFullPath(environment.PythonPath), Path.GetFullPath(catalogPython!), ignoreCase: true);
        Assert.Contains("not automatically importable", File.ReadAllText(environment.CatalogPath));
        Assert.DoesNotContain("Aliases are available in full-access host Python", File.ReadAllText(environment.CatalogPath));
        Assert.Contains(runtime.Tools.Definitions, tool => tool.Name == "python_library_docs");
        Assert.DoesNotContain(runtime.Tools.Definitions, tool => tool.Name == "python_libraries");
        Assert.DoesNotContain("Use python_libraries to check", runtime.CodeAct.Instructions);
        Assert.DoesNotContain("np/numpy", runtime.CodeAct.Instructions);
        var source = "import csv; from pathlib import Path; import requests; print(list(csv.reader(['a,b']))[0]); print(requests.__version__); print(Path('.').resolve().name)";
        var code = "r = call_tool('run_shell', exe=" + JsonSerializer.Serialize(environment.PythonPath) + ", args=['-c', " + JsonSerializer.Serialize(source) + "])\nassert r['ok'], r['error']\nprint(r['data']['stdout'])";
        var result = await runtime.CodeAct.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code }), runtime.CodeAct.CallToolAsync, default);
        Assert.True(result.Ok, result.ToJson());
        var output = JsonSerializer.SerializeToElement(result.Data); Assert.Equal(0, output.GetProperty("exitCode").GetInt32());
        Assert.Contains("['a', 'b']", output.GetProperty("stdout").GetString());
    }
}

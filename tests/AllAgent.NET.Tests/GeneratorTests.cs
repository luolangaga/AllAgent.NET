using AllAgent.NET.Core.Tools;
using AllAgent.NET.ToolGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class GeneratorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameShortNamesAndGlobalNamespaceGenerateCompilableTools(bool globalNamespace)
    {
        const string body = "public partial class Tools { [AgentTool(\"fixture\"), Description(\"fixture\")] public Task<ToolResult> Run() => Task.FromResult(ToolResult.Success(null)); }";
        var source = "using System.ComponentModel; using System.Threading.Tasks; using AllAgent.NET.Core.Tools; " +
            (globalNamespace ? body : "namespace First { " + body + " } namespace Second { " + body + " }");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(AgentToolAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create("GeneratorNamesProbe", [CSharpSyntaxTree.ParseText(source)], references, new(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new AgentToolGenerator().AsSourceGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(output.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(globalNamespace ? 1 : 2, driver.GetRunResult().GeneratedTrees.Length);
    }

    [Theory]
    [InlineData("DateTime when", true)]
    [InlineData("string path", false)]
    [InlineData("FileEdit[] edits", false)]
    [InlineData("string[] items", false)]
    [InlineData("int[] items", false)]
    [InlineData("DateTime[] items", true)]
    public void ToolContractIsCheckedAtCompileTime(string parameter, bool invalid)
    {
        var source = """
            using System;
            using System.ComponentModel;
            using System.Threading.Tasks;
            using AllAgent.NET.Core.Tools;
            namespace Sample;
            public partial class HostTools {
                [AgentTool("test"), Description("test tool")]
                public Task<ToolResult> Test([Description("typed parameter")]
            """ + parameter + ") => Task.FromResult(ToolResult.Success(null)); }";
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(AgentToolAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create("GeneratorProbe", [CSharpSyntaxTree.ParseText(source)], refs, new(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new AgentToolGenerator().AsSourceGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        if (invalid) Assert.Contains(diagnostics, d => d.Id == "LA001");
        else
        {
            Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
            Assert.Single(driver.GetRunResult().GeneratedTrees);
        }
    }
}

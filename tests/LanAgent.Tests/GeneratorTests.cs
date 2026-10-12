using LanAgent.Core.Tools;
using LanAgent.ToolGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LanAgent.Tests;

public sealed class GeneratorTests
{
    [Theory]
    [InlineData("DateTime when", true)]
    [InlineData("string path", false)]
    public void ToolContractIsCheckedAtCompileTime(string parameter, bool invalid)
    {
        var source = """
            using System;
            using System.ComponentModel;
            using System.Threading.Tasks;
            using LanAgent.Core.Tools;
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

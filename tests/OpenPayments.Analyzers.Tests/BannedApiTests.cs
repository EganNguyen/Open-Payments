using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace OpenPayments.Analyzers.Tests;

public sealed class BannedApiTests
{
    [Fact]
    public async Task S1_build_fails_on_task_result()
    {
        const string source = """
            using System.Threading.Tasks;

            public static class Probe
            {
                public static int Read(Task<int> task) => task.Result;
            }
            """;

        var diagnostics = await GetDiagnosticsAsync(source);

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Id == "RS0030"
                && diagnostic.GetMessage().Contains("Task", StringComparison.Ordinal));
    }

    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
    {
        var analyzerPath = Path.Combine(
            AppContext.BaseDirectory,
            "analyzers",
            "Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll");
        Assert.True(File.Exists(analyzerPath), $"Analyzer not found at {analyzerPath}.");

        var analyzerReference = new AnalyzerFileReference(analyzerPath, new AnalyzerLoader());
        var analyzers = analyzerReference.GetAnalyzers(LanguageNames.CSharp);
        Assert.NotEmpty(analyzers);

        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "BannedApiProbe",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var bannedSymbolsPath = Path.Combine(AppContext.BaseDirectory, "BannedSymbols.txt");
        Assert.True(File.Exists(bannedSymbolsPath), $"Banned symbols file not found at {bannedSymbolsPath}.");
        var additionalText = new BannedSymbolsText(bannedSymbolsPath);
        var options = new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(additionalText));

        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, options);
        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    private sealed class AnalyzerLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    private sealed class BannedSymbolsText(string path) : AdditionalText
    {
        private readonly string _text = File.ReadAllText(path);

        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(_text);
    }
}

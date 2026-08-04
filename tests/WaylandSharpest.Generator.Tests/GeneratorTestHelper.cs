using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using WaylandSharpest.Generator;

namespace WaylandSharpest.Generator.Tests;

internal static class GeneratorTestHelper
{
    public static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static (GeneratorDriverRunResult Result, Compilation Output) Run(
        params (string Path, string Text)[] files) =>
        Run(referenceRuntime: true, globalOptions: null, files);

    public static (GeneratorDriverRunResult Result, Compilation Output) Run(
        bool referenceRuntime,
        params (string Path, string Text)[] files) =>
        Run(referenceRuntime, globalOptions: null, files);

    /// <summary>Runs the generator with MSBuild properties visible as <c>build_property.*</c>.</summary>
    public static (GeneratorDriverRunResult Result, Compilation Output) RunWithOptions(
        Dictionary<string, string> globalOptions,
        params (string Path, string Text)[] files) =>
        Run(referenceRuntime: true, globalOptions, files);

    private static (GeneratorDriverRunResult Result, Compilation Output) Run(
        bool referenceRuntime,
        Dictionary<string, string>? globalOptions,
        params (string Path, string Text)[] files)
    {
        var references = TrustedPlatformReferences();
        if (referenceRuntime)
        {
            references = references.Add(MetadataReference.CreateFromFile(typeof(Wayland.WlProxy).Assembly.Location));
        }

        var compilation = CSharpCompilation.Create(
            "GeneratorTest",
            syntaxTrees: [],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false));

        var additionalTexts = files
            .Select(f => (AdditionalText)new InMemoryAdditionalText(f.Path, f.Text))
            .ToImmutableArray();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new WaylandProtocolGenerator().AsSourceGenerator()],
            optionsProvider: globalOptions is null ? null : new TestOptionsProvider(globalOptions))
            .AddAdditionalTexts(additionalTexts);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);
        return (driver.GetRunResult(), outputCompilation);
    }

    private sealed class TestOptionsProvider(Dictionary<string, string> globalOptions) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(globalOptions);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

        private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            public static readonly Options Empty = new([]);

            public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
        }
    }

    private static ImmutableArray<MetadataReference> TrustedPlatformReferences()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return
        [
            .. paths
                .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll"
                    or "netstandard.dll" or "System.Runtime.InteropServices.dll" or "System.Memory.dll"
                    or "System.Collections.dll" or "System.Collections.Concurrent.dll" or "System.Linq.dll")
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
        ];
    }

    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(text, System.Text.Encoding.UTF8);
    }
}

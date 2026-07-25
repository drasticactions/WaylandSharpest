using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using WaylandSharpest.Generator;

namespace WaylandSharpest.Generator.Tests;

internal static class GeneratorTestHelper
{
    public static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static (GeneratorDriverRunResult Result, Compilation Output) Run(
        params (string Path, string Text)[] files) =>
        Run(referenceRuntime: true, files);

    public static (GeneratorDriverRunResult Result, Compilation Output) Run(
        bool referenceRuntime,
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

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new WaylandProtocolGenerator())
            .AddAdditionalTexts(additionalTexts);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);
        return (driver.GetRunResult(), outputCompilation);
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

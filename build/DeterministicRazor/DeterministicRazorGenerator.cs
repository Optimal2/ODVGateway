using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.NET.Sdk.Razor.SourceGenerators;

namespace OpenModulePlatform.Build;

// The SDK generator embeds physical paths in its output. Csc maps document names,
// but cannot map the generated text embedded in portable PDBs (or its checksum).
// TagHelper IDs also include generated-text offsets, so rewriting output is too
// late. Map the generator's paths before generation, retaining the original text
// and item metadata. The selected SDK still owns all Razor compilation behavior.
[Generator]
public sealed class DeterministicRazorGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (!context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.OmpRazorCompilerDirectory", out var directory))
            throw new InvalidOperationException("The OMP Razor compiler directory was not provided.");
        var loader = AssemblyLoadContext.GetLoadContext(typeof(DeterministicRazorGenerator).Assembly)!;
        loader.LoadFromAssemblyPath(Path.Combine(directory, "Microsoft.AspNetCore.Razor.Utilities.Shared.dll"));
        loader.LoadFromAssemblyPath(Path.Combine(directory, "Microsoft.CodeAnalysis.Razor.Compiler.dll"));
        Generate(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Generate(GeneratorExecutionContext context)
    {
        var resolver = context.Compilation.Options.SourceReferenceResolver
            ?? throw new InvalidOperationException("Razor determinism requires a source resolver and PathMap.");
        string Map(string path) => (resolver.NormalizePath(path, null) ?? path).Replace('\\', '/');
        var files = context.AdditionalFiles.Select(file => new MappedText(file, Map(file.Path))).ToArray();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new RazorSourceGenerator().AsSourceGenerator()],
            additionalTexts: files,
            parseOptions: (CSharpParseOptions)context.ParseOptions,
            optionsProvider: new MappedOptionsProvider(context.AnalyzerConfigOptions, Map));
        var result = driver.RunGenerators(context.Compilation, context.CancellationToken).GetRunResult();
        foreach (var diagnostic in result.Diagnostics)
            context.ReportDiagnostic(diagnostic);

        foreach (var generated in result.Results.SelectMany(r => r.GeneratedSources))
        {
            // Materialize the SDK's custom SourceText before handing it to a
            // second driver. The outer compiler may read it concurrently.
            var source = generated.SourceText;
            context.AddSource(generated.HintName, SourceText.From(
                source.ToString(), source.Encoding ?? Encoding.UTF8, source.ChecksumAlgorithm));
        }
    }

    private sealed class MappedText(AdditionalText original, string path) : AdditionalText
    {
        public AdditionalText Original { get; } = original;
        public override string Path => path;
        public override SourceText? GetText(CancellationToken cancellationToken = default) => Original.GetText(cancellationToken);
    }

    private sealed class MappedOptionsProvider(AnalyzerConfigOptionsProvider original, Func<string, string> map) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new MappedGlobalOptions(original.GlobalOptions, map);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => original.GetOptions(tree);
        public override AnalyzerConfigOptions GetOptions(AdditionalText text) => original.GetOptions(text is MappedText mapped ? mapped.Original : text);
    }

    private sealed class MappedGlobalOptions(AnalyzerConfigOptions original, Func<string, string> map) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (!original.TryGetValue(key, out value!)) return false;
            if (key.Equals("build_property.MSBuildProjectDirectory", StringComparison.OrdinalIgnoreCase))
                value = map(value);
            return true;
        }
    }
}

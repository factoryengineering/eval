using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ApiConformance;

public sealed record CompileDiagnostic(string Id, string Severity, string Message, string Location)
{
    public override string ToString() => $"{Location}: {Severity} {Id}: {Message}";
}

public sealed record CompileResult(bool Success, Assembly? Assembly, IReadOnlyList<CompileDiagnostic> Diagnostics)
{
    public IEnumerable<CompileDiagnostic> Errors => Diagnostics.Where(d => d.Severity == nameof(DiagnosticSeverity.Error));
}

public static class GeneratedApp
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList());

    public static CompileResult Compile(params string[] sources) => Compile((IEnumerable<string>)sources);

    public static CSharpCompilation CreateCompilation(IEnumerable<string> sources)
    {
        var trees = sources.Select((source, index) =>
            CSharpSyntaxTree.ParseText(source, path: $"Generated{index}.cs")).ToList();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        return CSharpCompilation.Create($"Generated_{Guid.NewGuid():N}", trees, References.Value, options);
    }

    public static CompileResult Compile(IEnumerable<string> sources)
    {
        var compilation = CreateCompilation(sources);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        var diagnostics = emitted.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => new CompileDiagnostic(d.Id, d.Severity.ToString(), d.GetMessage(), d.Location.GetLineSpan().ToString()))
            .ToList();
        if (!emitted.Success) return new CompileResult(false, null, diagnostics);
        return new CompileResult(true, Assembly.Load(stream.ToArray()), diagnostics);
    }

    public static ApiHost Host(this CompileResult result) =>
        result.Assembly is { } assembly
            ? ApiHost.Start(assembly)
            : throw new InvalidOperationException("Cannot host a failed compilation:" + Environment.NewLine + string.Join(Environment.NewLine, result.Errors));
}

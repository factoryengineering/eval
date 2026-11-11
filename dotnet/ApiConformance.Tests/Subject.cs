using System.Text.Json;
using ApiConformance;

public sealed record Mutant(string Name, string File, string Find, string Replace, string[] ExpectedFailures);

public static class Subject
{
    public static readonly string EvalRoot = Path.Combine(FindRepositoryRoot(), "idempotent-writes", "evals");

    private static string BlogDirectory => Path.Combine(EvalRoot, "subjects", "blog");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent!;
        return directory.FullName;
    }

    public static DomainDescriptor Domain(string file) => DomainDescriptor.Load(Path.Combine(EvalRoot, "domains", file));

    public static IReadOnlyDictionary<string, string> BlogSources() =>
        Directory.GetFiles(BlogDirectory, "*.cs").ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText);

    public static IReadOnlyList<Mutant> Mutants() =>
        JsonSerializer.Deserialize<List<Mutant>>(File.ReadAllText(Path.Combine(EvalRoot, "mutants.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public static string[] Apply(IReadOnlyDictionary<string, string> sources, Mutant mutant)
    {
        var original = sources[mutant.File];
        if (!original.Contains(mutant.Find)) throw new InvalidOperationException($"Mutant '{mutant.Name}': find text not present in {mutant.File}.");
        return sources.Select(source => source.Key == mutant.File ? original.Replace(mutant.Find, mutant.Replace) : source.Value).ToArray();
    }

    public static ApiHost Start(IEnumerable<string> sources) => GeneratedApp.Compile(sources).Host();
}

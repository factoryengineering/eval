using System.Text.Json;
using ApiConformance;

public sealed record Mutant(string Name, string File, string Find, string Replace, string[] ExpectedFailures, string Domain = "blog");

public static class Subject
{
    public static readonly string EvalRoot = Path.Combine(FindRepositoryRoot(), "idempotent-writes", "evals");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent!;
        return directory.FullName;
    }

    public static DomainDescriptor Domain(string file) => DomainDescriptor.Load(Path.Combine(EvalRoot, "domains", file));

    public static IReadOnlyDictionary<string, string> Sources(string subject) =>
        Directory.GetFiles(Path.Combine(EvalRoot, "subjects", subject), "*.cs").ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText);

    public static IReadOnlyDictionary<string, string> BlogSources() => Sources("blog");

    public static string SubjectOf(string domainFile) => Path.GetFileNameWithoutExtension(domainFile).Replace("-root", "");

    public static IEnumerable<string> DomainFiles() =>
        Directory.GetFiles(Path.Combine(EvalRoot, "domains"), "*.json").Select(path => Path.GetFileName(path)!).Order();

    public static IReadOnlyList<Mutant> Mutants() =>
        JsonSerializer.Deserialize<List<Mutant>>(File.ReadAllText(Path.Combine(EvalRoot, "mutants.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public static string[] Apply(IReadOnlyDictionary<string, string> sources, Mutant mutant)
    {
        var original = sources[mutant.File];
        if (!original.Contains(mutant.Find)) throw new InvalidOperationException($"Mutant '{mutant.Name}': find text not present in {mutant.File}.");
        return sources.Select(source => source.Key == mutant.File ? original.Replace(mutant.Find, mutant.Replace) : source.Value).ToArray();
    }

    public static Func<ApiHost> Hosts(IEnumerable<string> sources)
    {
        var compiled = GeneratedApp.Compile(sources);
        return compiled.Host;
    }
}

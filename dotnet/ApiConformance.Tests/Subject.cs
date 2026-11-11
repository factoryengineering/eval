using System.Text.Json;
using ApiConformance;

public sealed record Edit(string File, string Find, string Replace);

public sealed record Mutant(string Name, string File, string Find, string Replace, string[] ExpectedFailures, string Domain = "blog", Edit[]? Extra = null, string[]? StaticExpectedFailures = null, string[]? LintExpectedFailures = null, string[]? SpecExpectedFailures = null);

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
        var files = sources.ToDictionary(source => source.Key, source => source.Value);
        foreach (var edit in new[] { new Edit(mutant.File, mutant.Find, mutant.Replace) }.Concat(mutant.Extra ?? []))
        {
            if (!files[edit.File].Contains(edit.Find)) throw new InvalidOperationException($"Mutant '{mutant.Name}': find text not present in {edit.File}.");
            files[edit.File] = files[edit.File].Replace(edit.Find, edit.Replace);
        }
        return sources.Keys.Select(name => files[name]).ToArray();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<ScenarioResult>>>> ScenarioRuns = new();

    public static Task<IReadOnlyList<ScenarioResult>> Scenarios(string key, string[] sources, DomainDescriptor domain) =>
        ScenarioRuns.GetOrAdd(key, _ => new(() => ProtocolScenarios.Run(ProtocolScenarios.All(domain), Hosts(sources), domain))).Value;

    public static Func<ApiHost> Hosts(IEnumerable<string> sources)
    {
        var compiled = GeneratedApp.Compile(sources);
        return compiled.Host;
    }
}

using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace EvalRunner;

public record Mutant(string Name, string Find, string Replace);
public record TestOutcome(string Name, bool Passed, string? Error);
public record MutantOutcome(string Name, string Status);
public record TestFacts(List<string> TestMethods, List<string> HelperMethods, int DiscardUsings, int ConnectionsOpenedInTests, List<string> Identifiers);
public record TestResult(string Id, bool Compiled, List<string> Diagnostics, List<string> Forbidden, List<TestOutcome> Tests, List<MutantOutcome> Mutants, TestFacts? Facts);

public static partial class SampleRunner
{
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    public static string WrapTests(string code)
    {
        var usings = new List<string>();
        var rest = new StringBuilder();
        foreach (var line in code.Replace("\r", "").Split('\n'))
            if (UsingLine().IsMatch(line.Trim())) usings.Add(line.Trim());
            else rest.AppendLine(line);
        var header = string.Join("\n", new[]
        {
            "using System;", "using System.Collections.Generic;", "using System.Linq;", "using System.Threading;",
            "using System.Threading.Tasks;", "using Microsoft.EntityFrameworkCore;", "using Microsoft.Data.Sqlite;",
            "using EfFixtures;", "using Xunit;",
        }.Concat(usings).Distinct());
        return $"{header}\n{rest}";
    }

    static CSharpCompilation CompileTests(string subject, SyntaxTree tests) =>
        CSharpCompilation.Create($"GeneratedTests_{Guid.NewGuid():N}", [CSharpSyntaxTree.ParseText(subject), tests], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    static Assembly? Load(CSharpCompilation compilation, out List<string> errors)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Id}: {d.GetMessage()}").Take(5).ToList();
        if (!emit.Success) return null;
        stream.Position = 0;
        return new AssemblyLoadContext(null, isCollectible: true).LoadFromStream(stream);
    }

    public static async Task<TestResult> RunTests(Request req)
    {
        var subject = req.Subject ?? throw new InvalidOperationException("a test request needs a subject");
        var tests = CSharpSyntaxTree.ParseText(WrapTests(req.Code));
        var compilation = CompileTests(subject, tests);
        var assembly = Load(compilation, out var errors);
        if (assembly is null) return new(req.Id, false, errors, [], [], [], null);

        var forbidden = Forbidden(compilation, tests);
        if (forbidden.Count > 0) return new(req.Id, true, [], forbidden, [], [], null);

        var outcomes = await RunAll(assembly, stopAtFirstFailure: false);
        var mutants = new List<MutantOutcome>();
        if (outcomes.Count > 0 && outcomes.All(o => o.Passed))
            foreach (var mutant in req.Mutants ?? [])
                mutants.Add(new(mutant.Name, await Mutate(subject, tests, mutant)));
        return new(req.Id, true, [], [], outcomes, mutants, TestFactsOf(compilation, tests));
    }

    static TestFacts TestFactsOf(CSharpCompilation compilation, SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var model = compilation.GetSemanticModel(tree);
        static bool IsTest(MethodDeclarationSyntax method) =>
            method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString() is "Fact" or "Theory" or "FactAttribute" or "TheoryAttribute" or "Xunit.Fact" or "Xunit.Theory");
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var testMethods = methods.Where(IsTest).ToList();
        var testClasses = testMethods.Select(method => method.Parent).ToHashSet();
        bool ImplementsInterface(MethodDeclarationSyntax method) =>
            model.GetDeclaredSymbol(method) is IMethodSymbol symbol &&
            symbol.ContainingType.AllInterfaces.SelectMany(contract => contract.GetMembers())
                .Any(member => SymbolEqualityComparer.Default.Equals(symbol.ContainingType.FindImplementationForInterfaceMember(member), symbol));
        var helpers = methods.Where(method => !IsTest(method) && !ImplementsInterface(method) && testClasses.Contains(method.Parent)).Select(method => method.Identifier.ValueText).ToList();
        var discards = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Count(statement => statement.UsingKeyword.RawKind != 0 && statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText.All(letter => letter == '_')));
        var connections = testMethods.Sum(method => method.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
            .Count(creation => model.GetTypeInfo(creation).Type?.Name == "SqliteConnection"));
        return new(testMethods.Select(method => method.Identifier.ValueText).ToList(), helpers, discards, connections, Identifiers(root));
    }

    static async Task<string> Mutate(string subject, SyntaxTree tests, Mutant mutant)
    {
        var index = subject.IndexOf(mutant.Find, StringComparison.Ordinal);
        if (index < 0) return "invalid";
        var mutated = subject[..index] + mutant.Replace + subject[(index + mutant.Find.Length)..];
        var assembly = Load(CompileTests(mutated, tests), out _);
        if (assembly is null) return "uncompilable";
        var outcomes = await RunAll(assembly, stopAtFirstFailure: true);
        return outcomes.Any(o => !o.Passed) ? "killed" : "survived";
    }

    static async Task<List<TestOutcome>> RunAll(Assembly assembly, bool stopAtFirstFailure)
    {
        var outcomes = new List<TestOutcome>();
        foreach (var type in assembly.GetTypes().Where(t => t.IsClass && t.IsPublic && !t.IsAbstract))
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<FactAttribute>().Any()).ToList();
            if (methods.Count == 0) continue;
            var fixtures = new Dictionary<Type, object>();
            try
            {
                foreach (var argument in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IClassFixture<>)).Select(i => i.GetGenericArguments()[0]))
                {
                    var fixture = Activator.CreateInstance(argument)!;
                    if (fixture is IAsyncLifetime lifetime) await lifetime.InitializeAsync();
                    fixtures[argument] = fixture;
                }
            }
            catch (Exception e)
            {
                outcomes.Add(new(type.Name, false, $"fixture: {Clip(e.Message)}"));
                continue;
            }
            foreach (var method in methods)
            {
                var cases = method.GetCustomAttributes<Xunit.Sdk.DataAttribute>().SelectMany(d => d.GetData(method)).ToList();
                var invocations = method.GetCustomAttribute<TheoryAttribute>() is null ? [[]] : cases;
                foreach (var args in invocations)
                {
                    var name = args.Length == 0 ? $"{type.Name}.{method.Name}" : $"{type.Name}.{method.Name}({string.Join(", ", args)})";
                    var outcome = await RunOne(type, method, args, fixtures, name);
                    outcomes.Add(outcome);
                    if (!outcome.Passed && stopAtFirstFailure) return outcomes;
                }
            }
            foreach (var fixture in fixtures.Values)
                if (fixture is IAsyncLifetime lifetime) await lifetime.DisposeAsync();
                else if (fixture is IAsyncDisposable disposable) await disposable.DisposeAsync();
                else if (fixture is IDisposable plain) plain.Dispose();
        }
        return outcomes;
    }

    static async Task<TestOutcome> RunOne(Type type, MethodInfo method, object?[] args, Dictionary<Type, object> fixtures, string name)
    {
        try
        {
            await Task.Run(async () =>
            {
                var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
                var instance = ctor.Invoke(ctor.GetParameters()
                    .Select(p => fixtures.TryGetValue(p.ParameterType, out var f) ? f : throw new InvalidOperationException($"cannot supply {p.ParameterType.Name}")).ToArray());
                try
                {
                    if (instance is IAsyncLifetime starting) await starting.InitializeAsync();
                    if (method.Invoke(instance, args) is Task task) await task;
                }
                finally
                {
                    if (instance is IAsyncLifetime lifetime) await lifetime.DisposeAsync();
                    else if (instance is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else if (instance is IDisposable disposable) disposable.Dispose();
                }
            }).WaitAsync(TestTimeout);
            return new(name, true, null);
        }
        catch (TimeoutException)
        {
            Poisoned = true;
            return new(name, false, "timeout after 20s");
        }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
            return new(name, false, $"{inner.GetType().Name}: {Clip(inner.Message)}");
        }
    }
}

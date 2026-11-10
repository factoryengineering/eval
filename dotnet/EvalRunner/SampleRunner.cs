using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using EfFixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EvalRunner;

public record Request(string Id, string Code, string? StringArg = null, int? IntArg = null, string? Subject = null, List<Mutant>? Mutants = null);
public record Invocation(string Method, string? Error);
public record SyntaxFacts(int QueryExpressions, List<string> MethodCalls, List<string> QueryableOperators, List<string> Identifiers, int AwaitedQueryLocals, int InlineAwaitedQueries);
public record Result(string Id, bool Compiled, List<string> Diagnostics, List<string> Forbidden, List<Invocation> Invoked, List<string> Sql, List<string> QueryStrings, SyntaxFacts? Syntax);

public static partial class SampleRunner
{
    public static bool Poisoned { get; private set; }

    static readonly string[] DeniedNamespaces =
        ["System.IO", "System.Net", "System.Reflection", "System.Runtime.InteropServices", "System.Diagnostics", "Microsoft.Win32", "System.Runtime.Loader"];

    static readonly string[] DeniedTypes = ["System.Environment", "System.AppDomain", "System.Activator"];

    static readonly Lazy<List<MetadataReference>> References = new(() =>
    {
        var refs = new List<MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            try { refs.Add(MetadataReference.CreateFromFile(path)); } catch (BadImageFormatException) { }
        return refs;
    });

    static readonly string[] SkipMethods = ["Equals", "GetHashCode", "ToString", "Deconstruct", "Clone", "<Clone>$", "GetType", "Finalize", "MemberwiseClone"];

    [GeneratedRegex(@"^using\s+(static\s+)?[\w.]+\s*;$")]
    private static partial Regex UsingLine();

    public static string Wrap(string code)
    {
        var usings = new List<string>();
        var rest = new StringBuilder();
        foreach (var line in code.Replace("\r", "").Split('\n'))
            if (UsingLine().IsMatch(line.Trim())) usings.Add(line.Trim());
            else rest.AppendLine(line);
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(rest.ToString()).GetRoot();
        var loose = root.Members.Any(m => m is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseNamespaceDeclarationSyntax));
        var header = string.Join("\n", new[]
        {
            "using System;", "using System.Collections.Generic;", "using System.Linq;", "using System.Threading;",
            "using System.Threading.Tasks;", "using Microsoft.EntityFrameworkCore;", "using EfFixtures;",
        }.Concat(usings));
        return loose ? $"{header}\npublic class Generated\n{{\n{rest}\n}}\n" : $"{header}\n{rest}";
    }

    public static async Task<Result> Run(Request req)
    {
        var source = Wrap(req.Code);
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            $"Generated_{Guid.NewGuid():N}", [tree], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}").Take(5).ToList();
        var facts = Facts(compilation, tree);
        if (errors.Count > 0) return new(req.Id, false, errors, [], [], [], [], facts);

        var forbidden = Forbidden(compilation, tree);
        if (forbidden.Count > 0) return new(req.Id, true, [], forbidden, [], [], [], facts);

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
            return new(req.Id, false, emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Id}: {d.GetMessage()}").Take(5).ToList(), [], [], [], [], facts);
        stream.Position = 0;
        var asm = new AssemblyLoadContext(null, isCollectible: true).LoadFromStream(stream);
        var (invoked, sql, queryStrings) = await Execute(asm, req);
        return new(req.Id, true, [], [], invoked, sql, queryStrings, facts);
    }

    public static Result Failure(string id, string message) => new(id, false, [message], [], [], [], [], null);

    static SyntaxFacts Facts(CSharpCompilation compilation, SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var model = compilation.GetSemanticModel(tree);
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        var calls = invocations
            .Select(i => i.Expression is MemberAccessExpressionSyntax m ? m.Name.Identifier.ValueText : null).OfType<string>().ToList();
        var operators = invocations
            .Where(i => i.Expression is MemberAccessExpressionSyntax && model.GetSymbolInfo(i).Symbol is IMethodSymbol { ContainingType: { Name: "Queryable", ContainingNamespace: { Name: "Linq" } } })
            .Select(i => ((MemberAccessExpressionSyntax)i.Expression).Name.Identifier.ValueText).ToList();
        int awaitedLocals = 0, awaitedInline = 0;
        foreach (var await in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            if (await.Expression is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }) continue;
            var receiver = model.GetTypeInfo(access.Expression).Type;
            if (receiver is null || !(receiver.Name == "IQueryable" || receiver.AllInterfaces.Any(i => i.Name == "IQueryable"))) continue;
            if (access.Expression is IdentifierNameSyntax) awaitedLocals++; else awaitedInline++;
        }
        var ids = new List<string>();
        foreach (var n in root.DescendantNodes())
            switch (n)
            {
                case VariableDeclaratorSyntax v: ids.Add(v.Identifier.ValueText); break;
                case ParameterSyntax p: ids.Add(p.Identifier.ValueText); break;
                case FromClauseSyntax f: ids.Add(f.Identifier.ValueText); break;
                case JoinClauseSyntax j: ids.Add(j.Identifier.ValueText); break;
                case JoinIntoClauseSyntax ji: ids.Add(ji.Identifier.ValueText); break;
                case QueryContinuationSyntax q: ids.Add(q.Identifier.ValueText); break;
                case LetClauseSyntax l: ids.Add(l.Identifier.ValueText); break;
                case ForEachStatementSyntax fe: ids.Add(fe.Identifier.ValueText); break;
            }
        return new(root.DescendantNodes().OfType<QueryExpressionSyntax>().Count(), calls, operators, ids.Where(i => i.Length > 0 && i != "_").ToList(), awaitedLocals, awaitedInline);
    }

    static List<string> Forbidden(CSharpCompilation compilation, SyntaxTree tree)
    {
        var model = compilation.GetSemanticModel(tree);
        var found = new SortedSet<string>();
        foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol is null) continue;
            var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            var ns = (type?.ContainingNamespace ?? symbol.ContainingNamespace)?.ToDisplayString() ?? "";
            if (ns.StartsWith("System.Diagnostics.CodeAnalysis")) continue;
            if (DeniedNamespaces.Any(d => ns == d || ns.StartsWith(d + "."))) found.Add($"{ns}.{type?.Name ?? symbol.Name}");
            else if (type is not null && DeniedTypes.Contains(type.ToDisplayString())) found.Add(type.ToDisplayString());
        }
        return [.. found];
    }

    static void Seed(BlogContext ctx)
    {
        ctx.Blogs.AddRange(
            new Blog
            {
                Name = "Alpha",
                Posts = [new Post { Title = "Intro to EF", Views = 150 }, new Post { Title = "Intro to LINQ", Views = 50 }, new Post { Title = "Advanced EF", Views = 500 }],
                Contributors = [new Contributor { Name = "Ann" }, new Contributor { Name = "Bo" }],
            },
            new Blog { Name = "Beta", Posts = [new Post { Title = "Intro to SQL", Views = 120 }], Contributors = [new Contributor { Name = "Cy" }] });
        ctx.SaveChanges();
    }

    static object? Synthesize(Type type, Request req, BlogContext ctx) =>
        type == typeof(BlogContext) ? ctx
        : type == typeof(CancellationToken) ? CancellationToken.None
        : type == typeof(string) ? req.StringArg ?? "Intro"
        : type == typeof(int) ? req.IntArg ?? 1
        : type == typeof(long) ? (long)(req.IntArg ?? 1)
        : type.IsValueType ? Activator.CreateInstance(type) : null;

    static async Task<(List<Invocation>, List<string>, List<string>)> Execute(Assembly asm, Request req)
    {
        var invoked = new List<Invocation>();
        var sql = new List<string>();
        var queryStrings = new List<string>();
        foreach (var type in asm.GetTypes().Where(t => t.IsClass && t.IsVisible && t.GetCustomAttribute<CompilerGeneratedAttribute>() is null && t.GetMethod("<Clone>$") is null))
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition && !m.IsAbstract && !SkipMethods.Contains(m.Name)))
            {
                var name = $"{type.Name}.{method.Name}";
                using var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                var capture = new Capture();
                using var ctx = new BlogContext(new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).AddInterceptors(capture).Options);
                ctx.Database.EnsureCreated();
                Seed(ctx);
                capture.On = true;
                try
                {
                    var instance = method.IsStatic ? null : Construct(type, req, ctx);
                    var args = method.GetParameters().Select(p => Synthesize(p.ParameterType, req, ctx)).ToArray();
                    var work = Task.Run(async () =>
                    {
                        var result = method.Invoke(instance, args);
                        if (result is Task task)
                        {
                            await task;
                            result = task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
                        }
                        if (result is IQueryable query)
                        {
                            try { queryStrings.Add(query.ToQueryString()); } catch (Exception) { }
                            foreach (var _ in query) { }
                        }
                    });
                    await work.WaitAsync(TimeSpan.FromSeconds(20));
                    invoked.Add(new(name, null));
                }
                catch (TimeoutException)
                {
                    Poisoned = true;
                    invoked.Add(new(name, "timeout after 20s"));
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
                    invoked.Add(new(name, $"{inner.GetType().Name}: {Clip(inner.Message)}"));
                }
                sql.AddRange(capture.Sql);
            }
        return (invoked, sql, queryStrings);
    }

    static object Construct(Type type, Request req, BlogContext ctx)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new InvalidOperationException($"{type.Name} has no public constructor");
        return ctor.Invoke(ctor.GetParameters().Select(p => Synthesize(p.ParameterType, req, ctx)).ToArray());
    }

    static string Clip(string s) => s.Length > 200 ? s[..200] : s;

    sealed class Capture : DbCommandInterceptor
    {
        public bool On;
        public readonly List<string> Sql = [];

        void Add(DbCommand command)
        {
            if (On && !command.CommandText.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
                lock (Sql) Sql.Add(command.CommandText);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Add(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Add(command);
            return new(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Add(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Add(command);
            return new(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Add(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Add(command);
            return new(result);
        }
    }
}

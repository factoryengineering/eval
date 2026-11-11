using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ApiConformance;

internal sealed record ActionFact(INamedTypeSymbol Controller, IMethodSymbol Method, MethodDeclarationSyntax Syntax, SemanticModel Model, string Verb, string Template)
{
    public string Name => $"{Controller.Name}.{Method.Name}";
    public IEnumerable<(string Name, string Constraint)> RouteParameters => Regex.Matches(Template, @"\{\*{0,2}(\w+)(?::([^}?=]+))?[^}]*\}").Select(m => (m.Groups[1].Value, m.Groups[2].Value));
}

internal sealed partial class SourceFacts
{
    private static readonly string[] Verbs = ["Get", "Post", "Put", "Delete"];
    private static readonly Regex IdLike = IdLikePattern();

    [GeneratedRegex("^(?i:id)$|Id$")]
    private static partial Regex IdLikePattern();

    public SourceFacts(StaticInputs inputs, HashSet<string> entityClrNames)
    {
        Compilation = GeneratedApp.CreateCompilation(inputs.Sources);
        EntityClrNames = entityClrNames;
        var controllerBase = Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase")!;
        foreach (var tree in Compilation.SyntaxTrees)
        {
            var model = Compilation.GetSemanticModel(tree);
            Models[tree] = model;
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not { } type || !Inherits(type, controllerBase)) continue;
                var prefix = Template(type.GetAttributes(), "RouteAttribute");
                foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(method) is not { DeclaredAccessibility: Accessibility.Public } symbol) continue;
                    foreach (var verb in Verbs)
                        if (symbol.GetAttributes().Any(a => a.AttributeClass?.Name == $"Http{verb}Attribute"))
                            Actions.Add(new(type, symbol, method, model, verb, string.Join('/', new[] { prefix, Template(symbol.GetAttributes(), $"Http{verb}Attribute") }.Where(t => t.Length > 0))));
                }
            }
        }
    }

    public Compilation Compilation { get; }
    public HashSet<string> EntityClrNames { get; }
    public Dictionary<SyntaxTree, SemanticModel> Models { get; } = [];
    public List<ActionFact> Actions { get; } = [];

    public static bool IsIdLike(string name) => IdLike.IsMatch(name);

    public bool IsEntity(ITypeSymbol type) => EntityClrNames.Contains(type.ToDisplayString());

    public IEnumerable<ITypeSymbol> Unwrap(ITypeSymbol type)
    {
        yield return type;
        if (type is IArrayTypeSymbol array) foreach (var inner in Unwrap(array.ElementType)) yield return inner;
        if (type is INamedTypeSymbol named) foreach (var argument in named.TypeArguments) foreach (var inner in Unwrap(argument)) yield return inner;
    }

    public IEnumerable<ITypeSymbol> ResponseTypes(ActionFact action)
    {
        foreach (var type in Unwrap(action.Method.ReturnType)) yield return type;
        foreach (var invocation in action.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            if (action.Model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { ContainingType: { } owner } && Inherits(owner, Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase")!))
                foreach (var argument in invocation.ArgumentList.Arguments)
                    if (action.Model.GetTypeInfo(argument.Expression).Type is { } type) foreach (var inner in Unwrap(type)) yield return inner;
        foreach (var creation in action.Syntax.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            if (action.Model.GetTypeInfo(creation).Type is { Name: "ObjectResult" } && creation.ArgumentList is { } list)
                foreach (var argument in list.Arguments)
                    if (action.Model.GetTypeInfo(argument.Expression).Type is { } type) foreach (var inner in Unwrap(type)) yield return inner;
    }

    public IEnumerable<INamedTypeSymbol> Dtos() =>
        Actions.SelectMany(a => a.Method.Parameters.Select(p => p.Type).Concat(ResponseTypes(a)))
            .SelectMany(Unwrap).OfType<INamedTypeSymbol>()
            .Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct && t.Locations.Any(l => l.IsInSource) && !IsEntity(t) && !Inherits(t, Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase")!))
            .Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>();

    public static bool IsIntegerType(ITypeSymbol type)
    {
        var inner = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;
        return inner.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16 or SpecialType.System_UInt32 or SpecialType.System_UInt64;
    }

    public static bool Inherits(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
        return false;
    }

    private static string Template(IEnumerable<AttributeData> attributes, string attribute) =>
        attributes.FirstOrDefault(a => a.AttributeClass?.Name == attribute)?.ConstructorArguments.FirstOrDefault().Value as string ?? "";

    public static bool MentionsHeader(SyntaxNode node, string header) =>
        node.DescendantNodes().OfType<LiteralExpressionSyntax>().Any(l => l.IsKind(SyntaxKind.StringLiteralExpression) && string.Equals(l.Token.ValueText, header, StringComparison.OrdinalIgnoreCase)) ||
        node.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(m => m.Name.Identifier.Text == header.Replace("-", ""));
}

internal static class SourceChecks
{
    public static IEnumerable<StaticCheckResult> Run(SourceFacts facts)
    {
        yield return RoutesUseGuid(facts);
        yield return NoEntityInResponses(facts);
        yield return NoIntIdInDtos(facts);
        yield return Sha256Used(facts);
        yield return EtagHeaderSet(facts);
        yield return IfMatchRequired(facts);
        yield return CancellationTokenPassed(facts);
        yield return ProblemDetailsUsed(facts);
        yield return NoCheckThenInsert(facts);
    }

    public static IReadOnlyList<string> RouteProblems(SourceFacts facts)
    {
        var problems = new List<string>();
        foreach (var action in facts.Actions)
        {
            foreach (var (name, constraint) in action.RouteParameters.Where(p => SourceFacts.IsIdLike(p.Name)))
            {
                var parameter = action.Method.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (parameter?.Type.ToDisplayString() != "System.Guid") problems.Add($"{action.Name}: route parameter {name} is bound to {parameter?.Type.ToDisplayString() ?? "nothing"}");
                else if (constraint.Length > 0 && constraint != "guid") problems.Add($"{action.Name}: route constraint {constraint} on {name}");
            }
            problems.AddRange(action.Method.Parameters.Where(p => SourceFacts.IsIdLike(p.Name) && SourceFacts.IsIntegerType(p.Type)).Select(p => $"{action.Name}: integer parameter {p.Name}"));
        }
        return problems;
    }

    public static IReadOnlyList<string> DtoProblems(SourceFacts facts) =>
        [.. facts.Dtos().SelectMany(t => t.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && SourceFacts.IsIdLike(p.Name) && SourceFacts.IsIntegerType(p.Type))
            .Select(p => $"{t.Name}.{p.Name} is {p.Type.Name}"))];

    private static StaticCheckResult RoutesUseGuid(SourceFacts facts)
    {
        var problems = RouteProblems(facts).ToList();
        if (facts.Actions.Count == 0) problems.Add("no controller actions found");
        return StaticChecks.Result("routes-use-guid", problems, $"{facts.Actions.Count} actions, every id-like route parameter is a Guid");
    }

    private static StaticCheckResult NoEntityInResponses(SourceFacts facts)
    {
        var problems = facts.Actions
            .SelectMany(a => facts.ResponseTypes(a).Where(facts.IsEntity).Select(t => $"{a.Name} returns entity {t.Name}"))
            .Distinct().ToList();
        return StaticChecks.Result("no-entity-in-responses", problems, "no return type or response argument is an EF entity");
    }

    private static StaticCheckResult NoIntIdInDtos(SourceFacts facts)
    {
        var problems = DtoProblems(facts);
        return StaticChecks.Result("no-int-id-in-dtos", problems, $"{facts.Dtos().Count()} request and response types carry no integer id property");
    }

    private static StaticCheckResult Sha256Used(SourceFacts facts)
    {
        var used = new HashSet<string>();
        var hashCodes = 0;
        foreach (var tree in facts.Compilation.SyntaxTrees)
        {
            var model = facts.Models[tree];
            foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
                if (model.GetSymbolInfo(identifier).Symbol is INamedTypeSymbol { ContainingNamespace: { } ns } type && ns.ToDisplayString() == "System.Security.Cryptography")
                    used.Add(type.Name);
            hashCodes += tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Count(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol { Name: "GetHashCode" });
        }
        var problems = new List<string>();
        if (!used.Contains("SHA256")) problems.Add("SHA256 is not used");
        problems.AddRange(used.Intersect(["MD5", "SHA1"]).Select(name => $"{name} is used"));
        if (hashCodes > 0) problems.Add($"GetHashCode is called {hashCodes} time(s)");
        return StaticChecks.Result("sha256-used-for-etag", problems, "SHA256 is used; no MD5, SHA1 or GetHashCode");
    }

    private static StaticCheckResult EtagHeaderSet(SourceFacts facts)
    {
        var problems = facts.Actions
            .Where(a => a.Verb is "Post" or "Put" || a.Verb == "Get" && a.RouteParameters.Any())
            .Where(a => !WritesETag(a.Syntax))
            .Select(a => $"{a.Name} ({a.Verb}) does not write the ETag header").ToList();
        return StaticChecks.Result("etag-header-set", problems, "create, read and replace actions write the ETag response header");
    }

    private static bool WritesETag(SyntaxNode body) =>
        body.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
            a.Left is MemberAccessExpressionSyntax { Name.Identifier.Text: "ETag" } member && member.ToString().Contains("Headers") ||
            a.Left is ElementAccessExpressionSyntax element && element.ToString().Contains("Headers") && element.ArgumentList.Arguments.Any(x => x.ToString().Contains("ETag", StringComparison.OrdinalIgnoreCase))) ||
        body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i =>
            i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Append" or "Add" or "TryAdd" or "Set" } access && access.ToString().Contains("Headers") &&
            i.ArgumentList.Arguments.FirstOrDefault()?.ToString().Contains("ETag", StringComparison.OrdinalIgnoreCase) == true);

    private static StaticCheckResult IfMatchRequired(SourceFacts facts)
    {
        var problems = facts.Actions.Where(a => a.Verb is "Put" or "Delete").Where(a =>
            !a.Method.Parameters.Any(p => p.GetAttributes().Any(x => x.AttributeClass?.Name == "FromHeaderAttribute" && x.NamedArguments.Any(n => n.Key == "Name" && string.Equals(n.Value.Value as string, "If-Match", StringComparison.OrdinalIgnoreCase)))
                && a.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => SymbolEqualityComparer.Default.Equals(a.Model.GetSymbolInfo(i).Symbol, p))) &&
            !SourceFacts.MentionsHeader((SyntaxNode?)a.Syntax.Body ?? a.Syntax.ExpressionBody!, "If-Match"))
            .Select(a => $"{a.Name} ({a.Verb}) never reads If-Match").ToList();
        return StaticChecks.Result("if-match-required", problems, "PUT and DELETE actions read the If-Match header");
    }

    private static StaticCheckResult CancellationTokenPassed(SourceFacts facts)
    {
        var problems = new List<string>();
        foreach (var action in facts.Actions.Where(a => a.Method.IsAsync || a.Method.ReturnType.Name is "Task" or "ValueTask"))
        {
            var token = action.Method.Parameters.FirstOrDefault(p => p.Type.ToDisplayString() == "System.Threading.CancellationToken");
            if (token is null) { problems.Add($"{action.Name} takes no CancellationToken"); continue; }
            var passed = action.Syntax.DescendantNodes().OfType<AwaitExpressionSyntax>()
                .SelectMany(a => a.DescendantNodesAndSelf().OfType<ArgumentSyntax>())
                .Any(argument => SymbolEqualityComparer.Default.Equals(action.Model.GetSymbolInfo(argument.Expression).Symbol, token));
            if (!passed) problems.Add($"{action.Name} does not pass {token.Name} to an awaited call");
        }
        return StaticChecks.Result("cancellation-token-passed", problems, "every async action takes a CancellationToken and passes it to an awaited call");
    }

    private static StaticCheckResult ProblemDetailsUsed(SourceFacts facts)
    {
        string[] names = ["ProblemDetails", "ValidationProblemDetails", "Problem", "ValidationProblem", "AddProblemDetails", "IProblemDetailsService"];
        var found = facts.Compilation.SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => names.Contains(i.Identifier.Text) && !i.Ancestors().OfType<AttributeListSyntax>().Any()).Select(i => facts.Models[tree].GetSymbolInfo(i).Symbol))
            .Where(symbol => symbol is not null).Select(symbol => symbol!.Name).Distinct().ToList();
        return StaticChecks.Result("problem-details-used", found.Count == 0 ? ["no ProblemDetails or Problem(...) usage"] : [], $"uses {string.Join(", ", found)}");
    }

    private static readonly string[] ExistenceQueries = ["AnyAsync", "FirstOrDefaultAsync", "SingleOrDefaultAsync", "CountAsync", "ContainsAsync"];

    private static StaticCheckResult NoCheckThenInsert(SourceFacts facts)
    {
        var problems = new List<string>();
        foreach (var tree in facts.Compilation.SyntaxTrees)
        {
            var model = facts.Models[tree];
            foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var added = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" or "AddAsync" } access && model.GetTypeInfo(access.Expression).Type is { Name: "DbSet" })
                    .Select(i => (Node: i, Entity: ((INamedTypeSymbol)model.GetTypeInfo(((MemberAccessExpressionSyntax)i.Expression).Expression).Type!).TypeArguments[0])).ToList();
                if (added.Count == 0) continue;
                var catches = method.DescendantNodes().OfType<CatchClauseSyntax>().Any(c => c.Declaration is null || model.GetTypeInfo(c.Declaration.Type).Type is { Name: "DbUpdateException" or "Exception" });
                if (catches) continue;
                foreach (var query in method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => i.Expression is MemberAccessExpressionSyntax access && ExistenceQueries.Contains(access.Name.Identifier.Text)))
                {
                    var receiver = model.GetTypeInfo(((MemberAccessExpressionSyntax)query.Expression).Expression).Type as INamedTypeSymbol;
                    if (receiver is null || receiver.TypeArguments.Length == 0) continue;
                    if (added.Any(a => a.Node.SpanStart > query.SpanStart && SymbolEqualityComparer.Default.Equals(a.Entity, receiver.TypeArguments[0])))
                        problems.Add($"{method.Identifier.Text}: awaited existence query on {receiver.TypeArguments[0].Name} before Add, no DbUpdateException handler (heuristic)");
                }
            }
        }
        return StaticChecks.Result("no-check-then-insert", [.. problems.Distinct()], "no existence query followed by an unguarded Add on the same entity (heuristic, advisory)", advisory: true);
    }
}

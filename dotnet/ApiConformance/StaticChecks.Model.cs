using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace ApiConformance;

internal sealed class ModelFacts : IDisposable
{
    public ModelFacts(StaticInputs inputs)
    {
        Domain = inputs.Domain;
        var scope = inputs.Host.Services.CreateScope();
        foreach (var type in inputs.Host.DbContextTypes)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(type);
            Contexts.Add(context);
        }
        Scope = scope;
        var names = new[] { Domain.EntityTypeName, Domain.Parent?.EntityTypeName }.Where(n => n is not null).ToList();
        Resources = Contexts.SelectMany(c => c.Model.GetEntityTypes())
            .Where(e => names.Count == 0 ? e.GetProperties().Any(p => p.ClrType == typeof(Guid)) : names.Contains(e.ClrType.Name))
            .ToList();
        EntityClrNames = Contexts.SelectMany(c => c.Model.GetEntityTypes()).Select(e => e.ClrType.FullName!).ToHashSet();
    }

    public void Dispose() => Scope.Dispose();

    public DomainDescriptor Domain { get; }
    public IServiceScope Scope { get; }
    public List<DbContext> Contexts { get; } = [];
    public IReadOnlyList<IEntityType> Resources { get; }
    public HashSet<string> EntityClrNames { get; }

    public DbContext ContextOf(IEntityType entity) => Contexts.First(c => c.Model.GetEntityTypes().Contains(entity));

    public List<Dictionary<string, object?>> Pragma(IEntityType entity, string pragma, string argument)
    {
        var connection = ContextOf(entity).Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma}(\"{argument.Replace("\"", "")}\")";
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        return rows;
    }

    public static bool IsInteger(Type type) => (Nullable.GetUnderlyingType(type) ?? type) is { } t && (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(uint) || t == typeof(ulong));

    public IEnumerable<IProperty> PublicIdCandidates(IEntityType entity) =>
        entity.GetProperties().Where(p => p.ClrType == typeof(Guid) && !p.IsPrimaryKey() && !p.IsForeignKey());

    public IEnumerable<IProperty> EtagTokens(IEntityType entity) =>
        entity.GetProperties().Where(p => p.IsConcurrencyToken && p.ClrType == typeof(string));
}

internal static class ModelChecks
{
    public static IEnumerable<StaticCheckResult> Run(ModelFacts facts)
    {
        yield return Check(facts, "public-id-unique-index", PublicIdUniqueIndex, "each resource has a Guid property with a unique index (model and SQLite schema agree)");
        yield return Check(facts, "internal-key-int-hidden", InternalKeyIntHidden, "primary keys are int or long, foreign keys reference them, none is a Guid");
        yield return Check(facts, "etag-concurrency-token", EtagConcurrencyToken, "each resource has a string concurrency token");
        yield return Check(facts, "no-rowversion-etag", NoRowVersion, "no row version or numeric concurrency token");
        yield return Check(facts, "creation-hash-immutable", CreationHashImmutable, "each resource has a non-null property whose update throws or is ignored after save");
        yield return Check(facts, "tombstone-property", TombstoneProperty, "each resource has a nullable timestamp used by its query filter");
        yield return Check(facts, "etag-column-not-null", EtagColumnNotNull, "the ETag columns are NOT NULL in the SQLite schema");
        yield return ServerOwnedNotToken(facts);
    }

    private static StaticCheckResult Check(ModelFacts facts, string id, Func<ModelFacts, IEntityType, string?> check, string passEvidence)
    {
        var problems = facts.Resources.Select(entity => check(facts, entity)).Where(p => p is not null).Select(p => p!).ToList();
        if (facts.Resources.Count == 0) problems.Add("no resource entity types were found in the DbContext model");
        return StaticChecks.Result(id, problems, $"{passEvidence}: {string.Join(", ", facts.Resources.Select(e => e.ClrType.Name))}");
    }

    private static string? PublicIdUniqueIndex(ModelFacts facts, IEntityType entity)
    {
        var table = entity.GetTableName()!;
        var unique = facts.PublicIdCandidates(entity).Where(p =>
            entity.GetKeys().Any(k => !k.IsPrimaryKey() && k.Properties.SequenceEqual([p])) ||
            entity.GetIndexes().Any(i => i.IsUnique && i.Properties.SequenceEqual([p]))).ToList();
        if (unique.Count == 0) return $"{entity.ClrType.Name}: no Guid property has a unique index or alternate key";
        var inSchema = unique.Any(p => facts.Pragma(entity, "index_list", table)
            .Where(i => Convert.ToInt32(i["unique"]) == 1)
            .Any(i => facts.Pragma(entity, "index_info", (string)i["name"]!).Select(c => (string?)c["name"]).SequenceEqual([p.GetColumnName()])));
        return inSchema ? null : $"{entity.ClrType.Name}: the SQLite schema has no unique index on {string.Join(", ", unique.Select(p => p.Name))}";
    }

    private static string? InternalKeyIntHidden(ModelFacts facts, IEntityType entity)
    {
        var key = entity.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1) return $"{entity.ClrType.Name}: no single-column primary key";
        var property = key.Properties[0];
        if (!ModelFacts.IsInteger(property.ClrType)) return $"{entity.ClrType.Name}: the primary key {property.Name} is {property.ClrType.Name}, not int or long";
        var table = entity.GetTableName()!;
        foreach (var foreignKey in facts.Pragma(entity, "foreign_key_list", table))
        {
            var principal = facts.Resources.Concat(facts.Contexts.SelectMany(c => c.Model.GetEntityTypes())).FirstOrDefault(e => e.GetTableName() == (string)foreignKey["table"]!);
            if (principal?.FindPrimaryKey()?.Properties.Select(p => p.GetColumnName()).Contains((string?)foreignKey["to"]) != true)
                return $"{entity.ClrType.Name}: the foreign key {foreignKey["from"]} does not reference the integer primary key of {foreignKey["table"]}";
        }
        return null;
    }

    private static string? EtagConcurrencyToken(ModelFacts facts, IEntityType entity) =>
        facts.EtagTokens(entity).Any() ? null : $"{entity.ClrType.Name}: no string property is a concurrency token";

    private static string? NoRowVersion(ModelFacts facts, IEntityType entity)
    {
        var offending = entity.GetProperties().Where(p =>
            p.PropertyInfo?.GetCustomAttribute<TimestampAttribute>() is not null ||
            p.ValueGenerated.HasFlag(ValueGenerated.OnUpdate) && p.IsConcurrencyToken ||
            p.IsConcurrencyToken && (ModelFacts.IsInteger(p.ClrType) || p.ClrType == typeof(byte[]))).Select(p => p.Name).ToList();
        return offending.Count == 0 ? null : $"{entity.ClrType.Name}: row version or numeric concurrency token {string.Join(", ", offending)}";
    }

    private static string? CreationHashImmutable(ModelFacts facts, IEntityType entity) =>
        entity.GetProperties().Any(p => !p.IsNullable && p.ClrType == typeof(string) && p.GetAfterSaveBehavior() != PropertySaveBehavior.Save)
            ? null : $"{entity.ClrType.Name}: no non-null string property has AfterSaveBehavior Throw or Ignore";

    private static string? TombstoneProperty(ModelFacts facts, IEntityType entity)
    {
        var timestamps = entity.GetProperties().Where(p => p.IsNullable && (Nullable.GetUnderlyingType(p.ClrType) is { } t && (t == typeof(DateTime) || t == typeof(DateTimeOffset)))).ToList();
        var filters = entity.GetDeclaredQueryFilters().Select(f => f.Expression?.ToString() ?? "").ToList();
        return timestamps.Any(p => filters.Any(f => f.Contains($".{p.Name}"))) ? null : $"{entity.ClrType.Name}: no nullable timestamp property is used by a query filter";
    }

    private static string? EtagColumnNotNull(ModelFacts facts, IEntityType entity)
    {
        var columns = facts.Pragma(entity, "table_info", entity.GetTableName()!);
        var nullable = facts.EtagTokens(entity).Select(p => p.GetColumnName())
            .Where(name => columns.Any(c => (string?)c["name"] == name && Convert.ToInt32(c["notnull"]) == 0)).ToList();
        return nullable.Count == 0 ? null : $"{entity.ClrType.Name}: ETag column(s) {string.Join(", ", nullable)} are nullable";
    }

    private static StaticCheckResult ServerOwnedNotToken(ModelFacts facts)
    {
        var problems = new List<string>();
        foreach (var field in facts.Domain.ServerOwned ?? [])
        {
            var property = facts.Contexts.SelectMany(c => c.Model.GetEntityTypes()).Where(e => e.ClrType.Name == field.EntityTypeName)
                .SelectMany(e => e.GetProperties()).FirstOrDefault(p => string.Equals(p.Name, field.Field, StringComparison.OrdinalIgnoreCase));
            if (property is null) problems.Add($"{field.EntityTypeName}.{field.Field} is not mapped");
            else if (property.IsConcurrencyToken) problems.Add($"{field.EntityTypeName}.{property.Name} is a concurrency token");
        }
        return StaticChecks.Result("server-owned-not-concurrency-token", problems, $"{(facts.Domain.ServerOwned ?? []).Count} server-owned field(s), none a concurrency token");
    }
}

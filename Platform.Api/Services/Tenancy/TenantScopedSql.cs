using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Platform.Api.Services.Tenancy;

/// <summary>
/// Schema-derived SQL fragments shared by the tenant purger and the module data purge preview:
/// delimited table/column names and the "row belongs to tenant {0}" predicate. Identifiers come
/// from the EF model, never from user input; the tenant id is always the <c>{0}</c> parameter.
/// Raw SQL on purpose: it never depends on the ambient tenant query filter.
/// </summary>
internal sealed class TenantScopedSql(ISqlGenerationHelper helper)
{
    public string Delimit(string identifier) => helper.DelimitIdentifier(identifier);

    public string Table(IEntityType entity) =>
        helper.DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());

    public string Column(IEntityType entity, IProperty property) =>
        helper.DelimitIdentifier(property.GetColumnName(StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema()))!);

    public string Join(IForeignKey foreignKey, string dependentAlias, string principalAlias) =>
        string.Join(" AND ", foreignKey.Properties.Select((property, index) =>
            $"{dependentAlias}.{Column(foreignKey.DeclaringEntityType, property)} = "
            + $"{principalAlias}.{Column(foreignKey.PrincipalEntityType, foreignKey.PrincipalKey.Properties[index])}"));

    public string Scope(IEntityType entity, string alias)
    {
        var aliasCounter = 0;
        return Scope(entity, alias, ref aliasCounter);
    }

    public string Scope(IEntityType entity, string alias, ref int aliasCounter)
    {
        var tenantId = entity.FindProperty("TenantId");
        if (tenantId is not null)
            return $"{alias}.{Column(entity, tenantId)} = {{0}}";

        // An unscoped dependent belongs to the tenant through its scoped principal.
        var predicates = new List<string>();
        foreach (var fk in entity.GetForeignKeys())
        {
            var principal = fk.PrincipalEntityType;
            if (!HasTenantAncestor(principal, []))
                continue;
            var parentAlias = $"parent{++aliasCounter}";
            var parentScope = Scope(principal, parentAlias, ref aliasCounter);
            predicates.Add($"EXISTS (SELECT 1 FROM {Table(principal)} AS {parentAlias} "
                + $"WHERE {Join(fk, alias, parentAlias)} AND ({parentScope}))");
        }
        if (predicates.Count == 0)
            throw new InvalidOperationException($"No tenant scope for purge entity {entity.Name}.");
        return "(" + string.Join(" OR ", predicates) + ")";
    }

    public static bool HasTenantAncestor(IEntityType entity) => HasTenantAncestor(entity, []);

    private static bool HasTenantAncestor(IEntityType entity, HashSet<IEntityType> visited)
    {
        if (entity.FindProperty("TenantId") is not null)
            return true;
        return visited.Add(entity) && entity.GetForeignKeys()
            .Any(fk => HasTenantAncestor(fk.PrincipalEntityType, visited));
    }
}

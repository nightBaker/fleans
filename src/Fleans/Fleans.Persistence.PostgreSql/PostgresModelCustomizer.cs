using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Fleans.Persistence.PostgreSql;

/// <summary>
/// PostgreSQL-specific model customizer applied on top of the shared Fleans model.
///
/// <para>
/// For v1, the shared model already stores <see cref="DateTimeOffset"/> properties
/// natively (no string converter), which is correct for PostgreSQL where they map to
/// <c>timestamptz</c>. JSON/text columns are kept as <c>text</c> matching the SQLite
/// layout; migration to <c>jsonb</c> is deferred to a follow-up issue.
/// </para>
///
/// <para>
/// This customizer exists so that <c>ReplaceService&lt;IModelCustomizer, PostgresModelCustomizer&gt;()</c>
/// produces a distinct EF Core model-cache key, preventing any cross-provider model
/// bleed in processes that register both providers (e.g. integration tests).
/// </para>
/// </summary>
internal sealed class PostgresModelCustomizer : RelationalModelCustomizer
{
    public PostgresModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        // The shared model already stores DateTimeOffset columns natively, which is
        // exactly what PostgreSQL expects (timestamptz). No overrides needed for v1.
        base.Customize(modelBuilder, context);
    }
}

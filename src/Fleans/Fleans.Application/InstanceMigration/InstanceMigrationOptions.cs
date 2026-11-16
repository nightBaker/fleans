namespace Fleans.Application.InstanceMigration;

/// <summary>Limits of a batch instance migration (<c>Fleans:Migration</c>, #1069).</summary>
public sealed class InstanceMigrationOptions
{
    public const string SectionName = "Fleans:Migration";

    /// <summary>
    /// The most instances one request migrates (<c>Fleans:Migration:MaxBatchSize</c>, default 100).
    /// A longer list is rejected; "all running instances of a version" is paged with a
    /// continuation token.
    /// </summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>How many instances of a batch migrate at once (<c>Fleans:Migration:MaxParallelism</c>, default 16).</summary>
    public int MaxParallelism { get; set; } = 16;
}

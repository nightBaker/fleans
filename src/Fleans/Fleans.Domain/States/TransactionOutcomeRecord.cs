using System.Text.Json.Serialization;

namespace Fleans.Domain.States;

// [JsonConverter] is kept in Domain by analogy with [GenerateSerializer] — both are
// serialization infrastructure attributes accepted on domain types per project convention.
// System.Text.Json is a BCL namespace in .NET 10; no external package reference needed.
[GenerateSerializer]
public sealed record TransactionOutcomeRecord(
    [property: Id(0)][property: JsonConverter(typeof(JsonStringEnumConverter))] TransactionOutcome Outcome,
    [property: Id(1)] string? ErrorCode,
    [property: Id(2)] string? ErrorMessage);

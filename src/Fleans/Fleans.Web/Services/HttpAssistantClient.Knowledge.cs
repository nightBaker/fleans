using System.Net;
using Fleans.Assistant.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace Fleans.Web.Services;

/// <summary>What a knowledge call answered: the value, a 404, or the error to show.</summary>
public sealed record KnowledgeResult<T>(T? Value, AssistantError? Error, bool IsNotFound);

// The read-only knowledge index (#1025). Nothing is kept here: every call carries the circuit's user token
// and the backend limits the answer to what that user may read.
public sealed partial class HttpAssistantClient
{
    public Task<KnowledgeResult<List<KnowledgeSource>>> GetKnowledgeSourcesAsync(CancellationToken ct) =>
        KnowledgeAsync<List<KnowledgeSource>>("sources", [], ct);

    /// <param name="kinds">Limits the search to these <see cref="KnowledgeKinds"/>; empty searches all of them.</param>
    public Task<KnowledgeResult<List<KnowledgeHit>>> SearchKnowledgeAsync(string query, string? project, IEnumerable<string> kinds, CancellationToken ct) =>
        KnowledgeAsync<List<KnowledgeHit>>("search",
            [new("q", query), new("project", project), .. kinds.Select(k => new KeyValuePair<string, string?>("kind", k))], ct);

    public Task<KnowledgeResult<KnowledgeFilePage>> ListKnowledgeFilesAsync(string? project, string repo, string? cursor, CancellationToken ct) =>
        KnowledgeAsync<KnowledgeFilePage>("files", [new("project", project), new("repo", repo), new("cursor", cursor)], ct);

    public Task<KnowledgeResult<KnowledgeChunks>> GetKnowledgeChunksAsync(string repo, string path, CancellationToken ct) =>
        KnowledgeAsync<KnowledgeChunks>("chunks", [new("repo", repo), new("path", path)], ct);

    public Task<KnowledgeResult<List<KnowledgeHit>>> GetKnowledgeAdrsAsync(string? project, string? query, CancellationToken ct) =>
        KnowledgeAsync<List<KnowledgeHit>>("adrs", [new("project", project), new("q", query)], ct);

    private async Task<KnowledgeResult<T>> KnowledgeAsync<T>(string resource, List<KeyValuePair<string, string?>> query, CancellationToken ct)
    {
        // Unset parameters stay out of the URL: "all projects" is no project, not an empty one.
        var path = QueryHelpers.AddQueryString($"{AssistantProtocol.KnowledgePath}/{resource}",
            query.Where(p => !string.IsNullOrWhiteSpace(p.Value)));
        var (value, error, status) = await CallAsync<T>(HttpMethod.Get, path, ct);
        return new KnowledgeResult<T>(value, error, status == (int)HttpStatusCode.NotFound);
    }
}

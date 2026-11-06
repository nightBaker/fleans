using System.Net;
using Fleans.Assistant.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace Fleans.Web.Services;

/// <summary>What a knowledge call answered: the page, a 404, or the error to show.</summary>
public sealed record KnowledgeResult<T>(KnowledgePage<T>? Page, AssistantError? Error, bool IsNotFound);

// The read-only knowledge index (#1025). Nothing is kept here: every call carries the circuit's user token
// and the backend limits the answer to what that user may read.
public sealed partial class HttpAssistantClient
{
    public Task<KnowledgeResult<KnowledgeSource>> GetKnowledgeSourcesAsync(CancellationToken ct) =>
        KnowledgeAsync<KnowledgeSource>("sources", [], ct);

    /// <param name="kinds">Limits the search to these <see cref="KnowledgeKinds"/>; empty searches all of them.</param>
    public Task<KnowledgeResult<KnowledgeHit>> SearchKnowledgeAsync(string query, string? project, IEnumerable<string> kinds, CancellationToken ct) =>
        KnowledgeAsync<KnowledgeHit>("search",
            [new("q", query), new("project", project), .. kinds.Select(k => new KeyValuePair<string, string?>("kind", k))], ct);

    public Task<KnowledgeResult<KnowledgeFile>> ListKnowledgeFilesAsync(string? project, string repo, string? cursor, CancellationToken ct) =>
        KnowledgeAsync<KnowledgeFile>("files", [new("project", project), new("repo", repo), new("cursor", cursor)], ct);

    public Task<KnowledgeResult<KnowledgeHit>> GetKnowledgeChunksAsync(string repo, string path, CancellationToken ct) =>
        KnowledgeAsync<KnowledgeHit>("chunks", [new("repo", repo), new("path", path)], ct);

    public Task<KnowledgeResult<KnowledgeHit>> GetKnowledgeAdrsAsync(string? project, string? query, CancellationToken ct) =>
        KnowledgeAsync<KnowledgeHit>("adrs", [new("project", project), new("q", query)], ct);

    private async Task<KnowledgeResult<T>> KnowledgeAsync<T>(string resource, List<KeyValuePair<string, string?>> query, CancellationToken ct)
    {
        // Unset parameters stay out of the URL: "all projects" is no project, not an empty one.
        var path = QueryHelpers.AddQueryString($"{AssistantProtocol.KnowledgePath}/{resource}",
            query.Where(p => !string.IsNullOrWhiteSpace(p.Value)));
        var (page, error, status) = await CallAsync<KnowledgePage<T>>(HttpMethod.Get, path, ct);
        return new KnowledgeResult<T>(page, error, status == (int)HttpStatusCode.NotFound);
    }
}

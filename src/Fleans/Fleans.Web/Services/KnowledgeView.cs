using Fleans.Assistant.Contracts;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Fleans.Web.Services;

/// <summary>A directory of the Browse tree: subdirectories and files, each sorted by name.</summary>
public sealed record KnowledgeTreeDirectory(string Name, List<KnowledgeTreeDirectory> Directories, List<KnowledgeFile> Files);

/// <summary>A file to open in the chunk view; <paramref name="Line"/> is the chunk to show first.</summary>
public sealed record KnowledgeFileRef(string Repo, string Path, int? Line = null);

/// <summary>The Knowledge tab's pure view logic (#1025).</summary>
public static class KnowledgeView
{
    private const int SnippetLength = 300;

    /// <summary>The web URL of <paramref name="repo"/>, if the backend gave one.</summary>
    public static string? WebUrlOf(IEnumerable<KnowledgeSource> sources, string repo) =>
        sources.FirstOrDefault(s => s.WebUrl is not null && string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase))?.WebUrl;

    /// <summary>The link to a file at its commit in the git provider; <c>null</c> without a usable web URL.</summary>
    public static string? FileUrl(string? webUrl, string commitSha, string path, int? line = null)
    {
        if (!IsHttp(webUrl))
            return null;
        var segments = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var url = $"{webUrl!.TrimEnd('/')}/blob/{Uri.EscapeDataString(commitSha)}/{segments}";
        return line is > 0 ? $"{url}#L{line}" : url;
    }

    /// <summary>A commit as lists show it.</summary>
    public static string ShortCommit(string commitSha) => commitSha.Length > 8 ? commitSha[..8] : commitSha;

    /// <summary>The start of a chunk's content, for hit lists.</summary>
    public static string Snippet(string content)
    {
        var text = content.Trim();
        return text.Length <= SnippetLength ? text : text[..SnippetLength].TrimEnd() + "…";
    }

    /// <summary>Folds the flat file listing into a path tree.</summary>
    public static KnowledgeTreeDirectory BuildTree(IEnumerable<KnowledgeFile> files)
    {
        var root = new KnowledgeTreeDirectory("", [], []);
        foreach (var file in files)
        {
            var parts = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var directory = root;
            foreach (var part in parts.SkipLast(1))
            {
                var next = directory.Directories.Find(d => d.Name == part);
                if (next is null)
                {
                    next = new KnowledgeTreeDirectory(part, [], []);
                    directory.Directories.Add(next);
                }
                directory = next;
            }
            directory.Files.Add(file);
        }
        Sort(root);
        return root;
    }

    private static void Sort(KnowledgeTreeDirectory directory)
    {
        directory.Directories.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        directory.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        directory.Directories.ForEach(Sort);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .Build();

    /// <summary>
    /// Renders a doc or ADR chunk to HTML. Repository content is untrusted and ends up in the Admin UI's page: raw
    /// HTML is escaped, only http(s) and mailto links survive (relative ones would resolve against the Admin UI),
    /// and images become links, so opening a document never calls a third party.
    /// </summary>
    public static string MarkdownToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            if (!IsSafeLink(autolink.Url))
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
        }
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            link.IsImage = false;
            if (IsSafeLink(link.Url))
                continue;
            // Keep the words, drop the link.
            while (link.FirstChild is { } child)
            {
                child.Remove();
                link.InsertBefore(child);
            }
            link.Remove();
        }
        return document.ToHtml(Pipeline);
    }

    internal static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsSafeLink(string? url) =>
        IsHttp(url) || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeMailto);
}

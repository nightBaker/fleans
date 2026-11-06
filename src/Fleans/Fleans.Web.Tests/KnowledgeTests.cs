using System.Net;
using System.Security.Claims;
using Fleans.Assistant.Contracts;
using Fleans.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleans.Web.Tests;

[TestClass]
public class KnowledgeAccessTests
{
    private static HttpResponseMessage Features(string json) => StubAssistantBackend.Json(HttpStatusCode.OK, json);

    private static KnowledgeAccess Access(StubAssistantBackend backend, string? endpoint = "http://assistant.test") =>
        new(backend.CreateClient(), Options.Create(new AssistantOptions { Endpoint = endpoint }), NullLogger<KnowledgeAccess>.Instance);

    [TestMethod]
    public async Task NoAssistantConfigured_NoTab_AndNoCall()
    {
        // Arrange
        var backend = new StubAssistantBackend();

        // Act
        var available = await Access(backend, endpoint: null).IsAvailableAsync(CancellationToken.None);

        // Assert
        Assert.IsFalse(available);
        Assert.AreEqual(0, backend.CapabilitiesCalls);
    }

    [TestMethod]
    public async Task V1Backend_WithoutCapabilities_NoTab()
    {
        Assert.IsFalse(await Access(new StubAssistantBackend()).IsAvailableAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task CapabilitiesWithoutKnowledge_NoTab()
    {
        // Arrange
        var backend = new StubAssistantBackend().StoringChats();

        // Act + Assert
        Assert.IsFalse(await Access(backend).IsAvailableAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task CapabilitiesWithKnowledge_Tab_AskedOncePerCircuit()
    {
        // Arrange
        var backend = new StubAssistantBackend { Capabilities = () => Features("""{"contractVersion":2,"features":["knowledge"]}""") };
        var access = Access(backend);

        // Act
        var first = await access.IsAvailableAsync(CancellationToken.None);
        var second = await access.IsAvailableAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(first);
        Assert.IsTrue(second);
        Assert.AreEqual(1, backend.CapabilitiesCalls);
    }

    [TestMethod]
    public async Task BackendCouldNotTell_NoTab_AskedAgainLater()
    {
        // Arrange
        var backend = new StubAssistantBackend { Capabilities = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        var access = Access(backend);

        // Act
        var whileDown = await access.IsAvailableAsync(CancellationToken.None);
        backend.Capabilities = () => Features("""{"contractVersion":2,"features":["knowledge"]}""");
        var afterwards = await access.IsAvailableAsync(CancellationToken.None);

        // Assert
        Assert.IsFalse(whileDown);
        Assert.IsTrue(afterwards);
    }

    [TestMethod]
    public async Task ClientFailure_NoTab_InsteadOfThrowingIntoTheShell()
    {
        // Arrange: a broken token handler, say — CallAsync only catches the expected failures.
        var backend = new StubAssistantBackend { Capabilities = () => throw new InvalidOperationException("boom") };

        // Act + Assert
        Assert.IsFalse(await Access(backend).IsAvailableAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task TheAnswerIsNotSharedBetweenCircuits()
    {
        // Arrange: the backend decides per user; each circuit has its own scoped KnowledgeAccess.
        var backend = new StubAssistantBackend { Capabilities = () => Features("""{"contractVersion":2,"features":["knowledge"]}""") };
        var firstUser = await Access(backend).IsAvailableAsync(CancellationToken.None);
        backend.Capabilities = () => Features("""{"contractVersion":2,"features":[]}""");

        // Act
        var secondUser = await Access(backend).IsAvailableAsync(CancellationToken.None);

        // Assert
        Assert.IsTrue(firstUser);
        Assert.IsFalse(secondUser);
    }
}

[TestClass]
public class KnowledgeClientTests
{
    private const string HitJson = """
        {"items":[{"repo":"bank/pay","branch":"main","commitSha":"abcdef1234567","kind":"adr","path":"docs/adr/1.md",
        "symbol":null,"title":"Use Postgres","content":"# Use Postgres","line":3}]}
        """;

    [TestMethod]
    public async Task Search_EncodesTheQuery_RepeatsKinds_AndReadsTheHit()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondJson(HitJson);

        // Act
        var result = await backend.CreateClient().SearchKnowledgeAsync("retry & backoff", "payments", ["doc", "adr"], CancellationToken.None);

        // Assert
        var (request, _) = backend.Calls.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("http://assistant.test/base/api/ai/knowledge/search?q=retry%20%26%20backoff&project=payments&kind=doc&kind=adr",
            request.RequestUri!.AbsoluteUri);
        var hit = result.Page!.Items.Single();
        Assert.AreEqual(("bank/pay", "docs/adr/1.md", 3, "abcdef1234567"), (hit.Repo, hit.Path, hit.Line, hit.CommitSha));
        Assert.IsNull(hit.Symbol);
    }

    [TestMethod]
    public async Task UnsetParameters_StayOutOfTheUrl()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondJson("""{"items":[]}""");

        // Act
        await backend.CreateClient().GetKnowledgeAdrsAsync(project: null, query: "  ", CancellationToken.None);

        // Assert
        Assert.AreEqual("http://assistant.test/base/api/ai/knowledge/adrs", backend.Calls.Single().Request.RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task Files_PassesTheCursor_AndReturnsTheNextOne()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondJson(
            """{"items":[{"path":"src/A.cs","kind":"code","title":"A","chunks":2}],"nextCursor":"page3"}""");

        // Act
        var result = await backend.CreateClient().ListKnowledgeFilesAsync("payments", "bank/pay", "page2", CancellationToken.None);

        // Assert
        StringAssert.Contains(backend.Calls.Single().Request.RequestUri!.Query, "repo=bank%2Fpay");
        StringAssert.Contains(backend.Calls.Single().Request.RequestUri!.Query, "cursor=page2");
        Assert.AreEqual("page3", result.Page!.NextCursor);
        Assert.AreEqual(2, result.Page.Items.Single().Chunks);
    }

    [TestMethod]
    public async Task Sources_ReadChunkCountsPerKind_AndTheOptionalWebUrl()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondJson("""
            {"items":[{"projectId":"payments","repo":"bank/pay","branch":"main","commitSha":"abc","engineVersion":"1.0",
            "indexedAt":"2026-11-05T10:00:00Z","chunks":5,"hasEmbeddings":true,"chunksByKind":{"doc":2,"code":3}}]}
            """);

        // Act
        var result = await backend.CreateClient().GetKnowledgeSourcesAsync(CancellationToken.None);

        // Assert
        var source = result.Page!.Items.Single();
        Assert.AreEqual(3, source.ChunksByKind[KnowledgeKinds.Code]);
        Assert.IsTrue(source.HasEmbeddings);
        Assert.IsNull(source.WebUrl);
    }

    [TestMethod]
    public async Task Chunks_404_IsNotFound()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondStatus(HttpStatusCode.NotFound);

        // Act
        var result = await backend.CreateClient().GetKnowledgeChunksAsync("bank/pay", "gone.md", CancellationToken.None);

        // Assert
        Assert.IsTrue(result.IsNotFound);
        Assert.IsNull(result.Page);
    }

    [TestMethod]
    public async Task Server5xx_IsAnError_NotNotFound()
    {
        // Arrange
        var backend = new StubAssistantBackend().RespondStatus(HttpStatusCode.InternalServerError);

        // Act
        var result = await backend.CreateClient().GetKnowledgeSourcesAsync(CancellationToken.None);

        // Assert
        Assert.IsFalse(result.IsNotFound);
        Assert.AreEqual(AssistantErrorCodes.Unavailable, result.Error!.Code);
    }
}

[TestClass]
public class AssistantTokenHandlerTests
{
    private sealed class Terminal : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpContext SignedIn(string? token)
    {
        var properties = new AuthenticationProperties();
        if (token is not null)
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = token }]);
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("test")), properties, "test")));
        return new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider(),
        };
    }

    [TestMethod]
    public async Task EachCallCarriesTheTokenOfTheCurrentUser()
    {
        // Arrange: one handler instance (IHttpClientFactory pools them) serving two users in turn.
        var accessor = new HttpContextAccessor();
        var terminal = new Terminal();
        using var invoker = new HttpMessageInvoker(new AssistantTokenHandler(accessor) { InnerHandler = terminal });
        var request = () => new HttpRequestMessage(HttpMethod.Get, "http://assistant.test/api/ai/knowledge/sources");

        // Act + Assert
        accessor.HttpContext = SignedIn("alice-token");
        await invoker.SendAsync(request(), CancellationToken.None);
        Assert.AreEqual("Bearer alice-token", terminal.Authorization);

        accessor.HttpContext = SignedIn("bob-token");
        await invoker.SendAsync(request(), CancellationToken.None);
        Assert.AreEqual("Bearer bob-token", terminal.Authorization);

        accessor.HttpContext = SignedIn(token: null);
        await invoker.SendAsync(request(), CancellationToken.None);
        Assert.IsNull(terminal.Authorization);
    }
}

[TestClass]
public class KnowledgeViewTests
{
    [TestMethod]
    [DataRow("https://gitlab.test/bank/pay", 12, "https://gitlab.test/bank/pay/blob/abc123/docs/my%20file.md#L12")]
    [DataRow("https://github.com/o/r/", null, "https://github.com/o/r/blob/abc123/docs/my%20file.md")]
    public void FileUrl_PointsAtTheFileAtItsCommit(string web, int? line, string expected) =>
        Assert.AreEqual(expected, KnowledgeView.FileUrl(web, "abc123", "docs/my file.md", line));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("javascript:alert(1)")]
    [DataRow("ssh://git@gitlab.test/bank/pay")]
    [DataRow("bank/pay")]
    public void FileUrl_NoUsableWebUrl_NoLink(string? web) =>
        Assert.IsNull(KnowledgeView.FileUrl(web, "abc123", "a.md"));

    [TestMethod]
    public void BuildTree_FoldsPathsIntoSortedDirectories()
    {
        // Arrange
        KnowledgeFile File(string path) => new(path, "doc", path, 1);

        // Act
        var root = KnowledgeView.BuildTree([File("src/b/Z.cs"), File("README.md"), File("src/A.cs"), File("src/a/B.cs")]);

        // Assert
        Assert.AreEqual("README.md", root.Files.Single().Path);
        var src = root.Directories.Single();
        Assert.AreEqual("src", src.Name);
        Assert.AreEqual("src/A.cs", src.Files.Single().Path);
        CollectionAssert.AreEqual(new[] { "a", "b" }, src.Directories.Select(d => d.Name).ToArray());
    }

    [TestMethod]
    public void Snippet_CutsLongContentOnly()
    {
        Assert.AreEqual("short", KnowledgeView.Snippet("  short\n"));
        var snippet = KnowledgeView.Snippet(new string('x', 1000));
        Assert.AreEqual(301, snippet.Length);
        StringAssert.EndsWith(snippet, "…");
    }

    [TestMethod]
    public void Markdown_RendersFormatting_AndKeepsWebLinks()
    {
        // Act
        var html = KnowledgeView.MarkdownToHtml("# Title\n\n**bold** [site](https://example.test/a) <https://example.test/b>\n\n|a|\n|-|\n|1|\n");

        // Assert
        StringAssert.Contains(html, "<h1>Title</h1>");
        StringAssert.Contains(html, "<strong>bold</strong>");
        StringAssert.Contains(html, "href=\"https://example.test/a\"");
        StringAssert.Contains(html, "href=\"https://example.test/b\"");
        StringAssert.Contains(html, "<table>");
    }

    [TestMethod]
    [DataRow("<script>alert(1)</script>", "<script")]
    [DataRow("<img src=x onerror=alert(1)>", "<img")]
    [DataRow("text <b onclick=alert(1)>x</b>", "<b ")]
    [DataRow("[click](javascript:alert(1))", "href")]
    [DataRow("[click](JaVaScRiPt:alert(1))", "href")]
    [DataRow("[click](data:text/html;base64,PHNjcmlwdD4=)", "href")]
    [DataRow("<javascript:alert(1)>", "href")]
    [DataRow("[click][r]\n\n[r]: javascript:alert(1)\n", "href")]
    [DataRow("[relative](../other.md)", "href")]
    [DataRow("![pixel](https://tracker.test/p.png)", "<img")]
    public void Markdown_NeutralizesUntrustedContent(string markdown, string forbidden) =>
        Assert.IsFalse(KnowledgeView.MarkdownToHtml(markdown).Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            KnowledgeView.MarkdownToHtml(markdown));

    [TestMethod]
    public void Markdown_DroppedLinkKeepsItsWords_AndImageBecomesALink()
    {
        StringAssert.Contains(KnowledgeView.MarkdownToHtml("[click here](javascript:alert(1))"), "click here");
        StringAssert.Contains(KnowledgeView.MarkdownToHtml("![pixel](https://site.test/p.png)"), "href=\"https://site.test/p.png\"");
    }
}

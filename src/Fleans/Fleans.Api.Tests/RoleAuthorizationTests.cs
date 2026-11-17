using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Fleans.Api.Authorization;
using Fleans.Api.Controllers;
using Fleans.Application;
using Fleans.Application.Bpmn;
using Fleans.Application.ExternalTasks;
using Fleans.Application.ExternalTasks.Grpc;
using Fleans.Application.InstanceMigration;
using Fleans.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Orleans;

namespace Fleans.Api.Tests;

/// <summary>
/// The role-to-endpoint matrix (#720) through the real HTTP pipeline: JWT bearer validation of
/// self-issued tokens, the controllers' <c>[Authorize(Policy = ...)]</c> attributes and the policies
/// registered by <see cref="ApiAuthorizationExtensions"/>, wired as <c>Program.cs</c> wires them.
/// Services behind the controllers are substitutes; the tests assert only who gets past
/// authorization (403/401 vs anything else).
/// </summary>
[TestClass]
public class RoleAuthorizationTests
{
    private const string Issuer = "https://idp.test/realms/fleans";
    private const string Audience = "fleans-api";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("fleans-rbac-test-signing-key-32bytes!!"));
    private static readonly Guid Id = Guid.NewGuid();

    private const string GrpcCompleteTask = "/fleans.externaltasks.v1.ExternalTaskWorker/CompleteTask";

    /// <summary>Every API endpoint and the least role that may call it.</summary>
    private static readonly (string Method, string Path, string MinRole)[] Matrix =
    [
        // Tasklist — user and above.
        ("GET", "/UserTasks", FleansRoles.User),
        ("GET", $"/UserTasks/{Id}", FleansRoles.User),
        ("POST", $"/UserTasks/{Id}/claim", FleansRoles.User),
        ("POST", $"/UserTasks/{Id}/unclaim", FleansRoles.User),
        ("POST", $"/UserTasks/{Id}/complete", FleansRoles.User),
        ("POST", $"/UserTasks/{Id}/fail", FleansRoles.User),
        ("POST", $"/UserTasks/{Id}/cancel", FleansRoles.User),

        // Instance lifecycle and read access — operator and above.
        ("POST", "/Execution/start", FleansRoles.Operator),
        ("POST", "/Execution/message", FleansRoles.Operator),
        ("POST", "/Execution/signal", FleansRoles.Operator),
        ("POST", "/Execution/evaluate-conditions", FleansRoles.Operator),
        ("POST", "/Execution/complete-activity", FleansRoles.Operator),
        ("POST", "/Execution/fail-activity", FleansRoles.Operator),
        ("GET", $"/Execution/{Id}/activities/{Id}/variables", FleansRoles.Operator),
        ("GET", $"/Instances/{Id}/state", FleansRoles.Operator),
        ("GET", "/Definitions", FleansRoles.Operator),
        ("GET", "/Definitions/latest?keys=k", FleansRoles.Operator),
        ("GET", "/Definitions/k/instances", FleansRoles.Operator),
        ("GET", "/Definitions/k/1/instances", FleansRoles.Operator),
        ("GET", "/Definitions/k/versions/1/migration-preview", FleansRoles.Operator),
        ("GET", "/custom-tasks", FleansRoles.Operator),
        ("GET", "/custom-tasks/t", FleansRoles.Operator),
        ("GET", "/Deployments/requests", FleansRoles.Operator),
        ("GET", $"/Deployments/requests/{Id}", FleansRoles.Operator),
        ("GET", $"/Deployments/requests/{Id}/bpmn", FleansRoles.Operator),
        ("GRPC", GrpcCompleteTask, FleansRoles.Operator),

        // Definition and cluster management — admin only.
        ("POST", "/Definitions/deploy", FleansRoles.Admin),
        ("POST", "/Definitions/disable", FleansRoles.Admin),
        ("POST", "/Definitions/enable", FleansRoles.Admin),
        ("POST", "/Instances/purge", FleansRoles.Admin),
        ("POST", $"/Instances/{Id}/migrate", FleansRoles.Admin),
        ("POST", "/Instances/migrate", FleansRoles.Admin),
        ("PUT", "/custom-tasks/t/allowed-processes", FleansRoles.Admin),
        // Submitting deploys at once when no approvals are required, so it is a deploy.
        ("POST", "/Deployments/requests", FleansRoles.Admin),
        ("POST", $"/Deployments/requests/{Id}/withdraw", FleansRoles.Admin),
        ("POST", $"/Deployments/requests/{Id}/approve", FleansRoles.Admin),
        ("POST", $"/Deployments/requests/{Id}/reject", FleansRoles.Admin),
        ("POST", $"/Deployments/requests/{Id}/retry", FleansRoles.Admin),
    ];

    private static readonly string[] RoleRank = [FleansRoles.User, FleansRoles.Operator, FleansRoles.Admin];

    private WebApplication? _app;

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    [TestMethod]
    [DataRow(FleansRoles.User)]
    [DataRow(FleansRoles.Operator)]
    [DataRow(FleansRoles.Admin)]
    public async Task EachRole_Gets403OutOfRole_AndPassesInRole(string role)
    {
        // Arrange
        var client = await StartAsync(authEnabled: true);
        var token = Token(("roles", role));
        var failures = new List<string>();

        // Act
        foreach (var (method, path, minRole) in Matrix)
        {
            using var response = await SendAsync(client, method, path, token);
            var allowed = Array.IndexOf(RoleRank, role) >= Array.IndexOf(RoleRank, minRole);
            var forbidden = response.StatusCode == HttpStatusCode.Forbidden;
            if (response.StatusCode == HttpStatusCode.Unauthorized || allowed == forbidden)
                failures.Add($"{method} {path} as {role}: {(int)response.StatusCode} (expected {(allowed ? "allowed" : "403")})");
        }

        // Assert
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public async Task AuthenticatedWithoutAnyRole_Gets403Everywhere()
    {
        // Arrange
        var client = await StartAsync(authEnabled: true);
        var token = Token(("roles", "viewer"), ("groups", "managers"));

        // Act + Assert
        foreach (var (method, path, _) in Matrix)
        {
            using var response = await SendAsync(client, method, path, token);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {path}");
        }
    }

    [TestMethod]
    public async Task Anonymous_Gets401Everywhere()
    {
        // Arrange
        var client = await StartAsync(authEnabled: true);

        // Act + Assert
        foreach (var (method, path, _) in Matrix)
        {
            using var response = await SendAsync(client, method, path, token: null);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {path}");
        }
    }

    [TestMethod]
    public async Task ConfiguredRoleClaim_IsTheOneRead()
    {
        // Arrange — roles under a custom claim name; the default `roles` claim is ignored.
        var client = await StartAsync(authEnabled: true, roleClaim: "fleans_roles");

        // Act
        using var custom = await SendAsync(client, "POST", "/Definitions/disable", Token(("fleans_roles", FleansRoles.Admin)));
        using var defaultName = await SendAsync(client, "POST", "/Definitions/disable", Token(("roles", FleansRoles.Admin)));

        // Assert
        Assert.AreNotEqual(HttpStatusCode.Forbidden, custom.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, defaultName.StatusCode);
    }

    [TestMethod]
    public async Task RoleValues_AreCaseSensitive()
    {
        // Arrange
        var client = await StartAsync(authEnabled: true);

        // Act
        using var response = await SendAsync(client, "POST", "/Definitions/disable", Token(("roles", "Admin")));

        // Assert
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AuthDisabled_AnonymousReachesEveryEndpoint()
    {
        // Arrange
        var client = await StartAsync(authEnabled: false);

        // Act + Assert
        foreach (var (method, path, _) in Matrix)
        {
            using var response = await SendAsync(client, method, path, token: null);
            Assert.IsFalse(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{method} {path}: {(int)response.StatusCode}");
        }
    }

    [TestMethod]
    public async Task EveryControllerAction_NamesARolePolicy()
    {
        // Arrange — guards against a new endpoint shipping with only the authenticated fallback.
        await StartAsync(authEnabled: true);
        var actions = _app!.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .ToList();

        // Act
        var unclassified = actions
            .Where(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy is not null))
            .Select(e => e.DisplayName)
            .ToList();

        // Assert
        Assert.IsNotEmpty(actions);
        Assert.IsEmpty(unclassified, string.Join(Environment.NewLine, unclassified));
        Assert.HasCount(actions.Count, Matrix.Where(m => m.Method != "GRPC").ToList(),
            "every controller action must have a row in the matrix (and the docs' role matrix)");
    }

    private async Task<HttpClient> StartAsync(bool authEnabled, string? roleClaim = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:RoleClaim"] = roleClaim,
        });

        if (authEnabled)
        {
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudience = Audience,
                    IssuerSigningKey = SigningKey,
                });
            builder.Services.AddSingleton<IUserGroupResolver, JwtUserGroupResolver>();
            builder.Services.AddSingleton<IUserIdResolver, JwtUserIdResolver>();
        }
        else
        {
            builder.Services.AddSingleton<IUserGroupResolver, BodyUserGroupResolver>();
            builder.Services.AddSingleton<IUserIdResolver, BodyUserIdResolver>();
        }
        builder.Services.AddFleansApiAuthorization(builder.Configuration, authEnabled);

        builder.Services.AddControllers().AddApplicationPart(typeof(ExecutionController).Assembly);
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(Substitute.For<IWorkflowCommandService>());
        builder.Services.AddSingleton(Substitute.For<IWorkflowQueryService>());
        builder.Services.AddSingleton(Substitute.For<IBpmnConverter>());
        builder.Services.AddSingleton(Substitute.For<IInstanceMigrationService>());
        builder.Services.AddSingleton(Substitute.For<IWorkflowInstancePurgeService>());
        builder.Services.AddSingleton(Substitute.For<IGrainFactory>());
        builder.Services.AddSingleton(new ExternalTaskWorkerConnections());
        builder.Services.AddOptions<ExternalTaskOptions>();
        builder.Services.AddSingleton(TimeProvider.System);

        _app = builder.Build();
        // Substitutes return null/default, so authorized calls may throw — report those as 500
        // instead of letting TestServer rethrow them.
        _app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch when (!ctx.Response.HasStarted) { ctx.Response.StatusCode = StatusCodes.Status500InternalServerError; }
        });
        if (authEnabled)
            _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
        _app.MapControllers();
        _app.MapGrpcService<ExternalTaskWorkerGrpcService>().RequireAuthorization(FleansPolicies.RequireOperator);
        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private static string Token(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity([new Claim("preferred_username", "tester"), .. claims.Select(c => new Claim(c.Type, c.Value))]);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = identity,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string? token)
    {
        var request = method == "GRPC"
            ? new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new ByteArrayContent([0, 0, 0, 0, 0]) { Headers = { ContentType = new MediaTypeHeaderValue("application/grpc-web") } },
            }
            : new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method == "GET" ? null : new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }
}

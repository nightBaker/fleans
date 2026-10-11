using System.Security.Claims;
using Fleans.Api.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Fleans.Api.Tests;

[TestClass]
public class UserIdResolverTests
{
    [TestMethod]
    public void Body_ReturnsBodyUserId()
    {
        var result = new BodyUserIdResolver().Resolve(new DefaultHttpContext(), "alice");

        Assert.AreEqual(UserIdResolution.Resolved("alice"), result);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void Body_EmptyUserId_IsMissing(string? bodyUserId)
    {
        var result = new BodyUserIdResolver().Resolve(new DefaultHttpContext(), bodyUserId);

        Assert.AreEqual(UserIdResolutionStatus.Missing, result.Status);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Jwt_NoBodyUserId_UsesToken(string? bodyUserId)
    {
        var result = Jwt().Resolve(Authenticated(("preferred_username", "bob")), bodyUserId);

        Assert.AreEqual(UserIdResolution.Resolved("bob"), result);
    }

    [TestMethod]
    public void Jwt_MatchingBodyUserId_UsesToken()
    {
        var result = Jwt().Resolve(Authenticated(("preferred_username", "bob")), "bob");

        Assert.AreEqual(UserIdResolution.Resolved("bob"), result);
    }

    [TestMethod]
    [DataRow("alice")]
    [DataRow("BOB")]
    public void Jwt_DifferentBodyUserId_IsMismatch(string bodyUserId)
    {
        var result = Jwt().Resolve(Authenticated(("preferred_username", "bob")), bodyUserId);

        Assert.AreEqual(UserIdResolutionStatus.Mismatch, result.Status);
        Assert.IsNull(result.UserId);
    }

    [TestMethod]
    public void Jwt_TokenWithoutClaim_IsNoIdentityClaim()
    {
        var result = Jwt().Resolve(Authenticated(("email", "bob@example.com")), "bob");

        Assert.AreEqual(UserIdResolutionStatus.NoIdentityClaim, result.Status);
    }

    [TestMethod]
    public void Jwt_Unauthenticated_IsNoIdentityClaim_EvenWithBodyUserId()
    {
        var result = Jwt().Resolve(new DefaultHttpContext(), "alice");

        Assert.AreEqual(UserIdResolutionStatus.NoIdentityClaim, result.Status);
    }

    [TestMethod]
    public void Jwt_ConfiguredClaim_IsUsed()
    {
        var resolver = Jwt(("Authentication:UserIdClaim", "email"));

        var result = resolver.Resolve(
            Authenticated(("preferred_username", "bob"), ("email", "bob@example.com")), null);

        Assert.AreEqual(UserIdResolution.Resolved("bob@example.com"), result);
    }

    [TestMethod]
    public void Jwt_SubClaim_FallsBackToMappedNameIdentifier()
    {
        var resolver = Jwt(("Authentication:UserIdClaim", "sub"));

        var result = resolver.Resolve(Authenticated((ClaimTypes.NameIdentifier, "user-123")), null);

        Assert.AreEqual(UserIdResolution.Resolved("user-123"), result);
    }

    internal static JwtUserIdResolver Jwt(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    internal static DefaultHttpContext Authenticated(params (string Type, string Value)[] claims) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Bearer")),
        };
}

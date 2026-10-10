using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Pii.Web;
using Pii.Web.Security;
using Pii.Worker;
using Xunit;

namespace Pii.Web.Tests;

public sealed class BoundaryTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1:5080", true)]
    [InlineData("::1", "localhost:5080", true)]
    [InlineData("::ffff:127.0.0.1", "[::1]:5080", true)]
    [InlineData("192.168.0.1", "localhost", false)]
    [InlineData("127.0.0.1", "attacker.example", false)]
    [InlineData("127.0.0.1", "localhost.attacker.example", false)]
    public void DevelopmentIdentityRequiresBothLocalPeerAndHost(string peer, string host, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        Assert.Equal(allowed, WorkspaceSecurity.IsLocalRequest(context.Request, IPAddress.Parse(peer)));
        Assert.False(WorkspaceSecurity.IsLocalRequest(context.Request, null));
    }

    [Theory]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Original-Host")]
    public void DevelopmentIdentityRejectsProxySpoofing(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost");
        context.Request.Headers[header] = "127.0.0.1";
        Assert.False(WorkspaceSecurity.IsLocalRequest(context.Request, IPAddress.Loopback));
    }

    [Fact]
    public void OwnerRequiresAuthenticatedUniqueTenantAndObjectClaims()
    {
        var tenant = WorkspaceSecurity.DevelopmentTenant;
        var owner = WorkspaceSecurity.DevelopmentObject;
        var claims = new[] { new Claim("tid", tenant), new Claim("oid", owner) };
        Assert.True(WorkspaceSecurity.IsOwner(new(new ClaimsIdentity(claims, "validated")), tenant, owner));
        Assert.False(WorkspaceSecurity.IsOwner(new(new ClaimsIdentity(claims)), tenant, owner));
        Assert.False(WorkspaceSecurity.IsOwner(new(new ClaimsIdentity(claims, "validated")), Guid.NewGuid().ToString(), owner));
        Assert.False(WorkspaceSecurity.IsOwner(new(new ClaimsIdentity(claims, "validated")), tenant, Guid.NewGuid().ToString()));
        Assert.False(WorkspaceSecurity.IsOwner(new(new ClaimsIdentity(claims.Append(new Claim("oid", owner)), "validated")), tenant, owner));
    }

    [Theory]
    [InlineData("2026-01-04T05:59:59Z", null)]
    [InlineData("2026-01-04T06:00:00Z", "daily:vilnius:2026-01-04:v1")]
    [InlineData("2026-07-04T04:59:59Z", null)]
    [InlineData("2026-07-04T05:00:00Z", "daily:vilnius:2026-07-04:v1")]
    [InlineData("2026-07-04T06:15:00Z", "daily:vilnius:2026-07-04:v1")]
    [InlineData("2026-03-29T05:00:00Z", "daily:vilnius:2026-03-29:v1")]
    [InlineData("2026-10-25T05:00:00Z", null)]
    [InlineData("2026-10-25T06:00:00Z", "daily:vilnius:2026-10-25:v1")]
    [InlineData("2026-10-25T20:00:00Z", "daily:vilnius:2026-10-25:v1")]
    public void DailyKeyUsesVilniusDueTimeAndLocalDate(string now, string? expected) =>
        Assert.Equal(expected, DailySchedule.DueKey(DateTimeOffset.Parse(now)));

    [Fact]
    public void WorkerCommandsAreFiniteAndRejectUnknownOrContradictoryFlags()
    {
        Assert.False(WorkerCommand.Parse([]).Daily);
        Assert.True(WorkerCommand.Parse(["--daily"]).Daily);
        Assert.True(WorkerCommand.Parse(["--once", "--fixture-scenario"]).FixtureScenario);
        Assert.Throws<ArgumentException>(() => WorkerCommand.Parse(["--forever"]));
        Assert.Throws<ArgumentException>(() => WorkerCommand.Parse(["--once", "--daily"]));
        Assert.Throws<ArgumentException>(() => WorkerCommand.Parse(["--once", "--once"]));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/?token=secret")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("https://127.0.0.1/")]
    public void CitationLinksRejectUnsafeSchemesCredentialsAndQueries(string address) => Assert.Null(Display.Citation(address));

    [Theory]
    [InlineData(null)]
    [InlineData("http://account.blob.core.windows.net/keys/key.xml")]
    [InlineData("https://attacker.example/keys/key.xml")]
    [InlineData("https://account.blob.core.windows.net/keys/key.xml?sig=secret")]
    [InlineData("https://account.blob.core.windows.net/keys/nested/key.xml")]
    public void ProductionCookieKeysRequireCanonicalPrivateBlobConfiguration(string? address)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", Args = [] });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Pii:DataProtection:BlobUri"] = address });
        Assert.Throws<InvalidOperationException>(() => builder.AddWorkspaceCookieKeys());
    }

    [Theory]
    [InlineData("Production", "true")]
    [InlineData("Production", "false")]
    [InlineData("Staging", "true")]
    public void HostAuthenticationFailsClosedOutsideExplicitLocalDevelopment(string environment, string developmentIdentity)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = [] });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Pii:AllowDevelopmentIdentity"] = developmentIdentity });
        Assert.Throws<InvalidOperationException>(() => builder.AddWorkspaceSecurity());
    }
}
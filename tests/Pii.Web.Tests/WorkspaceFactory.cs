using System.Net;
using System.Security.Claims;
using Claim = System.Security.Claims.Claim;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pii.Application;
using Pii.Web.Security;

namespace Pii.Web.Tests;

public sealed class WorkspaceFactory : WebApplicationFactory<global::WebEntryPoint>
{
    public TestWorkspace Store { get; } = new();
    public IPAddress? Peer { get; set; } = IPAddress.Loopback;
    public bool WrongOwner { get; set; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pii:AllowDevelopmentIdentity"] = "true", ["Pii:Mode"] = "FixtureReplay",
            ["AllowedHosts"] = "localhost;127.0.0.1;[::1]"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWorkspaceStore>();
            services.RemoveAll<IArtifactStore>();
            services.RemoveAll<IWorkDispatch>();
            services.AddSingleton<IWorkspaceStore>(Store);
            services.AddSingleton<IArtifactStore>(Store);
            services.AddSingleton<IWorkDispatch>(Store);
            services.AddSingleton<IStartupFilter>(new PeerFilter(this));
            if (WrongOwner)
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "WrongOwner"; options.DefaultChallengeScheme = "WrongOwner"; options.DefaultForbidScheme = "WrongOwner"; })
                    .AddScheme<AuthenticationSchemeOptions, WrongOwnerHandler>("WrongOwner", _ => { });
        });
    }
    public HttpClient LocalClient() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://127.0.0.1"), AllowAutoRedirect = false, HandleCookies = true });
    private sealed class PeerFilter(WorkspaceFactory factory) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) => { context.Connection.RemoteIpAddress = factory.Peer; await continuation(); });
            next(app);
        };
    }
    private sealed class WrongOwnerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", WorkspaceSecurity.DevelopmentTenant), new Claim("oid", "33333333-3333-3333-3333-333333333333") }, Scheme.Name)), Scheme.Name)));
    }
}
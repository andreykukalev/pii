using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace Pii.Web.Security;

public static class WorkspaceSecurity
{
    public const string Policy = "WorkspaceOwner";
    public const string DevelopmentScheme = "LocalDevelopment";
    public const string DevelopmentTenant = "11111111-1111-1111-1111-111111111111";
    public const string DevelopmentObject = "22222222-2222-2222-2222-222222222222";

    public static bool IsLocalRequest(HttpRequest request, IPAddress? peer)
    {
        if (peer is null || !IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)) return false;
        if (request.Headers.Keys.Any(name => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase))) return false;
        var host = request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    public static void AddWorkspaceSecurity(this WebApplicationBuilder builder)
    {
        var developmentIdentity = builder.Configuration.GetValue<bool>("Pii:AllowDevelopmentIdentity");
        if (developmentIdentity && !builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Development identity is forbidden outside Development.");
        string tenant;
        string owner;
        if (developmentIdentity)
        {
            tenant = DevelopmentTenant;
            owner = DevelopmentObject;
            builder.Services.AddAuthentication(DevelopmentScheme)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentIdentityHandler>(DevelopmentScheme, _ => { });
        }
        else
        {
            tenant = RequireGuid(builder.Configuration, "Pii:Auth:AllowedTenantId");
            owner = RequireGuid(builder.Configuration, "Pii:Auth:AllowedObjectId");
            var configuredTenant = RequireGuid(builder.Configuration, "AzureAd:TenantId");
            RequireGuid(builder.Configuration, "AzureAd:ClientId");
            if (!string.Equals(tenant, configuredTenant, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AzureAd tenant must match the authorized tenant.");
            if (builder.Configuration["AzureAd:Instance"] != "https://login.microsoftonline.com/")
                throw new InvalidOperationException("AzureAd:Instance must use the Microsoft Entra authority.");
            if (string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientSecret"]))
                throw new InvalidOperationException("Configure the Entra web credential securely before startup.");
            var hosts = builder.Configuration["AllowedHosts"];
            if (string.IsNullOrWhiteSpace(hosts) || hosts.Contains('*'))
                throw new InvalidOperationException("Explicit AllowedHosts are required with Entra authentication.");
            builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
            builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.RequireHttpsMetadata = true;
                options.Events.OnTokenValidated = context =>
                {
                    if (context.Principal is null || !IsOwner(context.Principal, tenant, owner))
                        context.Fail("This identity is not authorized for the workspace.");
                    return Task.CompletedTask;
                };
            });
            builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = "__Host-Pii.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        }
        builder.Services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
                .RequireAssertion(context => IsOwner(context.User, tenant, owner)).Build();
            options.AddPolicy(Policy, policy);
            options.FallbackPolicy = policy;
            options.DefaultPolicy = policy;
        });
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = developmentIdentity ? "Pii.Antiforgery" : "__Host-Pii.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = developmentIdentity ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
    }

    public static bool IsOwner(ClaimsPrincipal principal, string tenant, string owner) =>
        principal.Identity?.IsAuthenticated == true
        && principal.FindAll("tid").Count() == 1 && principal.FindAll("oid").Count() == 1
        && string.Equals(principal.FindFirst("tid")?.Value, tenant, StringComparison.OrdinalIgnoreCase)
        && string.Equals(principal.FindFirst("oid")?.Value, owner, StringComparison.OrdinalIgnoreCase);

    private static string RequireGuid(IConfiguration configuration, string key) =>
        Guid.TryParse(configuration[key], out var value) && value != Guid.Empty
            ? value.ToString("D") : throw new InvalidOperationException($"Required identity setting is missing: {key}.");
}

public sealed class DevelopmentIdentityHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IWebHostEnvironment environment, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("Pii:AllowDevelopmentIdentity")
            || !WorkspaceSecurity.IsLocalRequest(Request, Context.Connection.RemoteIpAddress))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tid", WorkspaceSecurity.DevelopmentTenant),
            new Claim("oid", WorkspaceSecurity.DevelopmentObject),
            new Claim(ClaimTypes.Name, "Local Development")
        }, WorkspaceSecurity.DevelopmentScheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
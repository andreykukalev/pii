using Microsoft.AspNetCore.Http.Features;
using Pii.Application;
using Pii.Infrastructure;
using Pii.Web.Security;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024 + 65536);
builder.AddWorkspaceSecurity();
builder.AddWorkspaceCookieKeys();
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/", WorkspaceSecurity.Policy));
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 2 * 1024 * 1024);
builder.Services.AddPiiInfrastructure(builder.Configuration, builder.Environment.IsDevelopment(), worker: false);
builder.Services.AddWorkspaceTelemetry(builder.Configuration);
builder.Services.AddTransient<ImportCoordinator>();
builder.Services.AddTransient<RunCoordinator>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    await CookieKeys.EnsurePrivateAsync(builder.Configuration);
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.Use(async (context, next) =>
{
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'none'; style-src 'self' https://cdn.jsdelivr.net; img-src 'self' data:; font-src 'self' https://cdn.jsdelivr.net; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self' https://login.microsoftonline.com";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("Workspace operation unavailable. Check local storage services and sanitized operational diagnostics.");
    }
    finally
    {
        context.RequestServices.GetRequiredService<WorkspaceTelemetry>().Record("Web",
            context.Response.StatusCode < 500, System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages().RequireAuthorization(WorkspaceSecurity.Policy);
app.Run();

public sealed class WebEntryPoint { }
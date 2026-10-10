using System.Net;
using System.Text;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Pii.Application;
using Pii.Domain;
using Pii.Web.Pages;
using Pii.Web.Security;
using Xunit;

namespace Pii.Web.Tests;

public sealed class HttpWorkspaceTests
{
    private static string Token(string html) => new HtmlParser().ParseDocument(html).QuerySelector("input[name='__RequestVerificationToken']")?.GetAttribute("value")
        ?? throw new InvalidOperationException("Antiforgery token missing.");
    private static FormUrlEncodedContent Form(params (string Name, string Value)[] values) => new(values.Select(item => new KeyValuePair<string, string>(item.Name, item.Value)));

    [Theory]
    [InlineData("/")]
    [InlineData("/Portfolio")]
    [InlineData("/Imports")]
    [InlineData("/Research")]
    [InlineData("/Runs")]
    [InlineData("/Settings")]
    [InlineData("/Sample")]
    [InlineData("/Artifacts/original/11111111111111111111111111111111")]
    [InlineData("/Runs/11111111111111111111111111111111")]
    public async Task AllWorkspaceRoutesRejectAnonymousBeforeStorage(string route)
    {
        await using var factory = new WorkspaceFactory { Peer = IPAddress.Parse("192.0.2.2") };
        using var client = factory.LocalClient();
        client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes("forged owner")));
        var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Store.InitializationCalls);
        Assert.Equal(0, factory.Store.ArtifactReads);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Sample")]
    [InlineData("/Artifacts/report/11111111111111111111111111111111")]
    public async Task AuthenticatedWrongObjectIsForbidden(string route)
    {
        await using var factory = new WorkspaceFactory { WrongOwner = true };
        using var client = factory.LocalClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(0, factory.Store.InitializationCalls);
    }

    [Fact]
    public async Task LocalIdentityRejectsForwardedHeadersAndRemoteHost()
    {
        await using var factory = new WorkspaceFactory();
        using var client = factory.LocalClient();
        client.DefaultRequestHeaders.Add("Forwarded", "for=127.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/Sample")).StatusCode);
        client.DefaultRequestHeaders.Remove("Forwarded");
        client.DefaultRequestHeaders.Host = "attacker.example";
        var denied = await client.GetAsync("/Sample");
        Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized });
    }

    [Theory]
    [InlineData("/Runs?handler=Submit")]
    [InlineData("/Imports?handler=Confirm")]
    [InlineData("/Imports?handler=Upload")]
    [InlineData("/Runs/11111111111111111111111111111111?handler=Cancel")]
    [InlineData("/Runs/11111111111111111111111111111111?handler=Retry")]
    public async Task MutationFormsRequireAntiforgery(string route)
    {
        await using var factory = new WorkspaceFactory();
        using var client = factory.LocalClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route, Form(("AcceptSpend", "true")))).StatusCode);
        Assert.Equal(0, factory.Store.ConfirmationCalls);
        Assert.Empty(factory.Store.Runs);
    }

    [Fact]
    public async Task BriefEscapesUntrustedTextAndSeparatesModeAiAndRunStatus()
    {
        await using var factory = new WorkspaceFactory();
        var facts = new PortfolioReconstructor().Calculate(1, [], [], DateTimeOffset.UtcNow);
        var run = new AnalysisRun { Status = RunStatus.Partial, AiStatus = "DeterministicFallback", Mode = ExecutionMode.FixtureReplay, Revision = 1 };
        run.Brief = new(run.Id, 1, run.AsOf, run.Mode, run.AiStatus, facts, [new("A. Summary", "<script>alert('private')</script>")], [], [], [], null);
        factory.Store.Runs[run.Id] = run;
        using var client = factory.LocalClient();
        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("DeterministicFallback", html);
        Assert.Contains("FixtureReplay", html);
        Assert.Contains("Partial", html);
        Assert.Contains("script-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task SampleUploadPreviewDoesNotMutateAndConfirmationChecksBaseline()
    {
        await using var factory = new WorkspaceFactory();
        factory.Store.Portfolio = new(0, [], [], new(), DateTimeOffset.UtcNow, "EmptyTest");
        using var client = factory.LocalClient();
        var token = Token(await client.GetStringAsync("/Imports"));
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(token), "__RequestVerificationToken");
        upload.Add(new StringContent(SampleModel.Csv, Encoding.UTF8, "text/csv"), "Upload", "sample.csv");
        var uploaded = await client.PostAsync("/Imports?handler=Upload", upload);
        Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
        Assert.Equal(0, factory.Store.Portfolio.Revision);
        var preview = Assert.Single(factory.Store.Previews.Values);
        var html = await client.GetStringAsync(uploaded.Headers.Location);
        Assert.Contains("721.44", html);
        var rejected = await client.PostAsync("/Imports?handler=Confirm", Form(("__RequestVerificationToken", Token(html)), ("PreviewId", preview.Id), ("BaselineRevision", "99"), ("Acknowledge", "true")));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(0, factory.Store.ConfirmationCalls);
        var accepted = await client.PostAsync("/Imports?handler=Confirm", Form(("__RequestVerificationToken", Token(html)), ("PreviewId", preview.Id), ("BaselineRevision", "0"), ("Acknowledge", "true")));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal(1, factory.Store.ConfirmationCalls);
        Assert.Equal(1, factory.Store.Portfolio.Revision);
    }

    [Fact]
    public async Task RetryIsIdempotentNewRunAndPreservesOldLimits()
    {
        await using var factory = new WorkspaceFactory();
        var old = new AnalysisRun { Status = RunStatus.BudgetExceeded, ModelSubmissions = 9, ReservedUsd = .7m, ReservedTokens = 900, Generation = 3, AiStatus = "BudgetExceeded" };
        factory.Store.Runs[old.Id] = old;
        using var client = factory.LocalClient();
        var html = await client.GetStringAsync("/Runs/" + old.Id);
        var retryKey = Guid.NewGuid().ToString("N");
        var first = await client.PostAsync($"/Runs/{old.Id}?handler=Retry", Form(("__RequestVerificationToken", Token(html)), ("RetryKey", retryKey), ("AcceptSpend", "true")));
        var second = await client.PostAsync($"/Runs/{old.Id}?handler=Retry", Form(("__RequestVerificationToken", Token(html)), ("RetryKey", retryKey), ("AcceptSpend", "true")));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(2, factory.Store.Runs.Count);
        var retained = factory.Store.Runs[old.Id];
        Assert.Equal(9, retained.ModelSubmissions);
        Assert.Equal(.7m, retained.ReservedUsd);
        Assert.Equal(900, retained.ReservedTokens);
        Assert.Equal(3, retained.Generation);
        Assert.Equal(RunStatus.BudgetExceeded, retained.Status);
    }

    [Fact]
    public async Task DownloadsOnlyResolveCanonicalStoreReferences()
    {
        await using var factory = new WorkspaceFactory();
        var id = Guid.NewGuid().ToString("N");
        factory.Store.Previews[id] = new(id, 1, "hash", "originals/test.csv", [], [], [], 0, DateTimeOffset.UtcNow);
        factory.Store.Artifacts["originals/test.csv"] = Encoding.UTF8.GetBytes("private csv");
        using var client = factory.LocalClient();
        var response = await client.GetAsync("/Artifacts/original/" + id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Artifacts/raw/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Artifacts/original/not-canonical")).StatusCode);
        factory.Store.Previews[id] = factory.Store.Previews[id] with { OriginalBlob = "runs/raw-prompt.json" };
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Artifacts/original/" + id)).StatusCode);
        Assert.Equal(1, factory.Store.ArtifactReads);
    }

    [Theory]
    [InlineData("Production", "true")]
    [InlineData("Production", "false")]
    [InlineData("Staging", "true")]
    public void ProductionFailsClosedWithoutAuthOrWithDevelopmentIdentity(string environment, string development)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = [] });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Pii:AllowDevelopmentIdentity"] = development });
        Assert.Throws<InvalidOperationException>(() => builder.AddWorkspaceSecurity());
    }
}
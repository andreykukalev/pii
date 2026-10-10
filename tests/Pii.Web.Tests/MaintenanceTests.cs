using System.Net;
using AngleSharp.Html.Parser;
using Pii.Application;
using Xunit;

namespace Pii.Web.Tests;

public sealed class MaintenanceTests
{
    private static string Token(string html) => new HtmlParser().ParseDocument(html)
        .QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
    private static FormUrlEncodedContent Form(params (string Name, string Value)[] values) =>
        new(values.Select(item => new KeyValuePair<string, string>(item.Name, item.Value)));

    [Theory]
    [InlineData("/Maintenance")]
    [InlineData("/Settings")]
    public async Task PagesRejectAnonymousAndWrongOwnerBeforeStorage(string route)
    {
        await using var anonymous = new WorkspaceFactory { Peer = IPAddress.Parse("192.0.2.2") };
        using var first = anonymous.LocalClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync(route)).StatusCode);
        Assert.Equal(0, anonymous.Store.InitializationCalls);
        await using var forbidden = new WorkspaceFactory { WrongOwner = true };
        using var second = forbidden.LocalClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync(route)).StatusCode);
        Assert.Equal(0, forbidden.Store.InitializationCalls);
    }

    [Theory]
    [InlineData("/Maintenance?handler=Export")]
    [InlineData("/Maintenance?handler=Delete")]
    [InlineData("/Settings?handler=Policy")]
    public async Task PostsRequireAntiforgery(string route)
    {
        await using var factory = new WorkspaceFactory();
        using var client = factory.LocalClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route, Form())).StatusCode);
        Assert.Equal(0, factory.Store.DeletionCalls);
        Assert.Equal(0, factory.Store.PolicyCalls);
    }

    [Fact]
    public async Task ExportIsPrivateBoundedJsonWithCompleteEmbeddedEvidence()
    {
        await using var factory = new WorkspaceFactory();
        for (var index = 0; index < 101; index++)
        {
            var run = new AnalysisRun { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-index), Portfolio = factory.Store.Portfolio };
            run.Evidence.Add(new("evidence", "test", "<script>source</script>", "https://example.test", "fact",
                run.CreatedAt, run.CreatedAt, "complete private evidence", [], false, "test"));
            factory.Store.Runs[run.Id] = run;
        }
        using var client = factory.LocalClient();
        var html = await client.GetStringAsync("/Maintenance");
        var response = await client.PostAsync("/Maintenance?handler=Export", Form(("__RequestVerificationToken", Token(html)),
            ("ExpectedRevision", "1"), ("AcceptExportScope", "true")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var export = JsonDefaults.Parse<WorkspaceExport>(await response.Content.ReadAsStringAsync());
        Assert.Equal(100, export.Runs.Count);
        Assert.True(export.HistoryMayBeTruncated);
        Assert.All(export.Runs, run => Assert.Equal("complete private evidence", Assert.Single(run.Evidence).Text));
        Assert.Equal(0, factory.Store.ArtifactReads);
        Assert.Equal(101, factory.Store.Runs.Count);
    }

    [Fact]
    public async Task OversizedExportReturnsNoPartialDownload()
    {
        await using var factory = new WorkspaceFactory();
        var run = new AnalysisRun { Errors = [new string('x', WorkspaceMaintenance.MaximumExportBytes)] };
        factory.Store.Runs[run.Id] = run;
        using var client = factory.LocalClient();
        var token = Token(await client.GetStringAsync("/Maintenance"));
        var response = await client.PostAsync("/Maintenance?handler=Export", Form(("__RequestVerificationToken", token),
            ("ExpectedRevision", "1"), ("AcceptExportScope", "true")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Theory]
    [InlineData("0", true, true, "DELETE WORKSPACE", 409)]
    [InlineData("1", false, true, "DELETE WORKSPACE", 400)]
    [InlineData("1", true, false, "DELETE WORKSPACE", 400)]
    [InlineData("1", true, true, "DELETE", 400)]
    public async Task DeleteRequiresBoundRevisionAndExplicitAcknowledgements(string revision, bool exclusive, bool loss, string confirmation, int status)
    {
        await using var factory = new WorkspaceFactory();
        using var client = factory.LocalClient();
        var response = await DeleteAsync(client, revision, exclusive, loss, confirmation);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, factory.Store.DeletionCalls);
    }

    [Fact]
    public async Task ExpiredRunningClaimStillRefusesDeletion()
    {
        await using var factory = new WorkspaceFactory();
        var run = new AnalysisRun { Status = RunStatus.Running, ClaimExpires = DateTimeOffset.MinValue, CancelRequested = true };
        factory.Store.Runs[run.Id] = run;
        using var client = factory.LocalClient();
        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(client)).StatusCode);
        Assert.Equal(0, factory.Store.DeletionCalls);
        Assert.Single(factory.Store.Runs);
    }

    [Fact]
    public async Task PartialArtifactDeletionIsExplicitAndRetryCompletes()
    {
        await using var factory = new WorkspaceFactory();
        factory.Store.Artifacts["originals/private.csv"] = [1];
        var run = new AnalysisRun { Status = RunStatus.Cancelled };
        factory.Store.Runs[run.Id] = run;
        factory.Store.FailArtifactDeletion = true;
        using var client = factory.LocalClient();
        var partial = await DeleteAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
        var html = await partial.Content.ReadAsStringAsync();
        Assert.Contains("Partial deletion", html);
        Assert.DoesNotContain("sensitive diagnostic", html);
        Assert.Equal(0, factory.Store.Portfolio.Revision);
        Assert.Empty(factory.Store.Runs);
        Assert.Single(factory.Store.Artifacts);
        factory.Store.FailArtifactDeletion = false;
        var retry = await DeleteAsync(client, "0");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Empty(factory.Store.Artifacts);
        Assert.Contains("does not verify erasure", await retry.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string revision = "1", bool exclusive = true,
        bool loss = true, string confirmation = "DELETE WORKSPACE")
    {
        var token = Token(await client.GetStringAsync("/Maintenance"));
        return await client.PostAsync("/Maintenance?handler=Delete", Form(("__RequestVerificationToken", token),
            ("ExpectedRevision", revision), ("ExclusiveMaintenance", exclusive.ToString()),
            ("AcceptPermanentLoss", loss.ToString()), ("Confirmation", confirmation)));
    }
}
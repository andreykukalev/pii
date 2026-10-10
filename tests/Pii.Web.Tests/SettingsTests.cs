using System.Net;
using AngleSharp.Html.Parser;
using Pii.Application;
using Xunit;

namespace Pii.Web.Tests;

public sealed class SettingsTests
{
    [Fact]
    public async Task UnconfiguredPolicyHasNoNumericalDefaults()
    {
        await using var factory = new WorkspaceFactory();
        using var client = factory.LocalClient();
        var page = new HtmlParser().ParseDocument(await client.GetStringAsync("/Settings"));
        Assert.Equal("", page.QuerySelector("input[name='MaximumPositionPercent']")!.GetAttribute("value") ?? "");
        Assert.Equal("", page.QuerySelector("input[name='MaximumTacticalPercent']")!.GetAttribute("value") ?? "");
        Assert.NotNull(page.QuerySelector("a[href='/Maintenance']"));
    }

    [Theory]
    [InlineData("", "", "true", "1", 400)]
    [InlineData("0", "", "true", "1", 400)]
    [InlineData("101", "", "true", "1", 400)]
    [InlineData("12.5", "-1", "true", "1", 400)]
    [InlineData("12.5", "101", "true", "1", 400)]
    [InlineData("not-a-number", "", "true", "1", 400)]
    [InlineData("12.5", "", "false", "1", 400)]
    [InlineData("12.5", "", "true", "0", 409)]
    public async Task InvalidOrUnacknowledgedOrStalePolicyCannotSave(string position, string tactical, string accept, string revision, int status)
    {
        await using var factory = new WorkspaceFactory();
        using var client = factory.LocalClient();
        var response = await PostAsync(client, position, tactical, accept, revision);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, factory.Store.PolicyCalls);
        Assert.False(factory.Store.Portfolio.Policy.Configured);
    }

    [Fact]
    public async Task ExplicitPolicySavesAndKeepsSizingBlocked()
    {
        await using var factory = new WorkspaceFactory();
        using var client = factory.LocalClient();
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "12.5", "", "true", "1")).StatusCode);
        Assert.Equal(1, factory.Store.PolicyCalls);
        Assert.Equal(2, factory.Store.Portfolio.Revision);
        Assert.Null(factory.Store.Portfolio.Policy.MaximumTacticalPercent);
        var pending = Assert.Single(factory.Store.Runs.Values);
        Assert.Equal(factory.Store.Portfolio.Policy, pending.Portfolio!.Policy);
        Assert.False(Pii.Domain.RecommendationPolicyEvaluator.Evaluate(Pii.Domain.RecommendationCategory.BUY,
            Pii.Domain.Horizon.LongTerm, pending.Portfolio.Policy, true, true).Eligible);
        Assert.Contains("does not implement sizing", await client.GetStringAsync("/Settings"));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string position, string tactical, string accept, string revision)
    {
        var document = new HtmlParser().ParseDocument(await client.GetStringAsync("/Settings"));
        var token = document.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
        return await client.PostAsync("/Settings?handler=Policy", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["ExpectedRevision"] = revision,
            ["MaximumPositionPercent"] = position, ["MaximumTacticalPercent"] = tactical,
            ["AcceptInformationalPolicy"] = accept
        }));
    }
}
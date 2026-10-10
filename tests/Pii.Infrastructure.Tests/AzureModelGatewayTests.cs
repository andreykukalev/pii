using System.Net;
using System.Text.Json;
using OpenAI.Chat;
using Pii.Application;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Infrastructure.Tests;

public sealed class AzureModelGatewayTests
{
    private const string Schema = """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";
    private static AzureModelOptions Settings() => new()
    {
        Authorized = true, ProcessingAuthorized = true, Endpoint = "https://recorded.openai.azure.com/", Deployment = "recorded-deployment",
        ModelVersion = "recorded-version", ProcessingRegion = "local-recording", RateCardVersion = "test-not-a-real-price",
        InputUsdPerMillion = 1.25m, OutputUsdPerMillion = 5m, SupportsStrictSchemaAndTools = true, Authentication = "ApiKey", ApiKey = "synthetic-api-key"
    };
    private static ModelRequest Request(List<ModelMessage>? messages = null) => new(messages ?? [new("system", "Synthetic test only"), new("user", "{}")],
        [new("recorded_tool", "A recorded test tool", Schema, [])], Schema, 100, "run-test", "task-test");
    private static string Completion(string message = "\"content\":\"{}\"", string finish = "stop", bool usage = true) =>
        $$"""{"id":"chatcmpl-recorded","object":"chat.completion","created":1760000000,"model":"recorded","choices":[{"index":0,"message":{"role":"assistant",{{message}}},"finish_reason":"{{finish}}"}]{{(usage ? ",\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":10,\"total_tokens\":40}" : "")}}}""";

    [Fact]
    public async Task NativeToolHistoryStrictSchemasSequentialCallsAndUsageAreMapped()
    {
        using var transport = new RecordedHttp((_, _) =>
        {
            var response = RecordedHttp.Json(Completion());
            response.Headers.Add("apim-request-id", "provider-request-recorded");
            return response;
        });
        using var client = new HttpClient(transport);
        var gateway = new AzureModelGateway(Settings(), true, client);
        var reply = await gateway.CompleteAsync(Request([new("system", "Synthetic test only"), new("user", "{}"),
            new("assistant", "", [new("call-1", "recorded_tool", "{}")]), new("tool", "{}", ToolCallId: "call-1")]), ExecutionMode.Live, default);
        Assert.Equal(30L, reply.InputTokens);
        Assert.Equal(10L, reply.OutputTokens);
        Assert.Equal("{}", reply.Content);
        Assert.Equal("provider-request-recorded", reply.ProviderRequestId);
        using var request = JsonDocument.Parse(Assert.Single(transport.Bodies));
        var root = request.RootElement;
        Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.True(root.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.True(root.GetProperty("tools")[0].GetProperty("function").GetProperty("strict").GetBoolean());
        Assert.Equal("tool", root.GetProperty("messages")[3].GetProperty("role").GetString());
        Assert.Equal("call-1", root.GetProperty("messages")[3].GetProperty("tool_call_id").GetString());
        Assert.Contains("api-version=2024-10-21", transport.Uris[0].Query);
    }

    [Fact]
    public async Task MissingProviderUsageRemainsUnknownRatherThanZero()
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(Completion(usage: false)));
        using var client = new HttpClient(transport);
        var gateway = new AzureModelGateway(Settings(), true, client);
        var reply = await gateway.CompleteAsync(Request(), ExecutionMode.Live, default);
        Assert.Null(reply.InputTokens);
        Assert.Null(reply.OutputTokens);
        Assert.Null(reply.ProviderRequestId);
        Assert.Null(gateway.EstimateUsd(reply.InputTokens, reply.OutputTokens));
    }

    [Fact]
    public async Task NativeToolCallResponseIsReturnedWithoutExecution()
    {
        const string message = """
            "content":null,"tool_calls":[{"id":"call-recorded","type":"function","function":{"name":"recorded_tool","arguments":"{}"}}]
            """;
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(Completion(message, "tool_calls")));
        using var client = new HttpClient(transport);
        var reply = await new AzureModelGateway(Settings(), true, client).CompleteAsync(Request(), ExecutionMode.Live, default);
        Assert.Equal(new ModelToolCall("call-recorded", "recorded_tool", "{}"), Assert.Single(reply.ToolCalls));
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(429, "ModelRateLimitedNoAutomaticRetry")]
    [InlineData(401, "ModelAuthenticationDenied")]
    [InlineData(404, "ModelDeploymentNotFound")]
    public async Task ProviderErrorsAreSanitizedAndNeverRetried(int status, string code)
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json("""{"error":{"message":"synthetic-api-key confidential-payload","type":"recorded","code":"recorded"}}""", (HttpStatusCode)status));
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => new AzureModelGateway(Settings(), true, client).CompleteAsync(Request(), ExecutionMode.Live, default));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("synthetic-api-key", error.ToString());
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("length", "\"content\":\"{}\"", "ModelOutputTruncated")]
    [InlineData("content_filter", "\"content\":null", "ModelContentFiltered")]
    [InlineData("stop", "\"content\":null,\"refusal\":\"refused\"", "ModelRefusal")]
    public async Task RefusalFilteringAndTruncationAreNotSuccess(string finish, string message, string code)
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(Completion(message, finish)));
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ModelGatewayException>(() => new AzureModelGateway(Settings(), true, client).CompleteAsync(Request(), ExecutionMode.Live, default));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task DisabledActivationAndCancelledRequestsNeverSubmit()
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<ModelGatewayException>(() => new AzureModelGateway(new(), true, client).CompleteAsync(Request(), ExecutionMode.Live, default));
        await Assert.ThrowsAsync<ModelGatewayException>(() => new AzureModelGateway(Settings(), true, client).CompleteAsync(Request(), ExecutionMode.FixtureReplay, default));
        await Assert.ThrowsAsync<ModelGatewayException>(() => new AzureModelGateway(Settings(), true, client).CompleteAsync(Request(), ExecutionMode.ModelOnFixtures, default));
        await Assert.ThrowsAsync<ModelGatewayException>(() => new AzureModelGateway(Settings(), false, client).CompleteAsync(Request(), ExecutionMode.ModelOnFixtures, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AzureModelGateway(Settings(), true, client).CompleteAsync(Request(), ExecutionMode.Live, cancelled.Token));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public void HistoryRejectsMismatchedDuplicateOrUnresolvedToolIds()
    {
        Assert.Throws<ModelGatewayException>(() => AzureModelGateway.TranslateMessages([new("tool", "{}", ToolCallId: "missing")]));
        Assert.Throws<ModelGatewayException>(() => AzureModelGateway.TranslateMessages([new("assistant", "", [new("call", "recorded_tool", "{}")])]));
        Assert.Throws<ModelGatewayException>(() => AzureModelGateway.TranslateMessages([new("assistant", "", [new("call", "recorded_tool", "{}")]), new("tool", "{}", ToolCallId: "call"),
            new("assistant", "", [new("call", "recorded_tool", "{}")])]));
        Assert.IsType<ToolChatMessage>(AzureModelGateway.TranslateMessages([new("assistant", "", [new("call", "recorded_tool", "{}")]), new("tool", "{}", ToolCallId: "call")])[1]);
    }

    [Fact]
    public void CostUsesExplicitPerMillionRatesAndUnknownInputsStayUnknown()
    {
        var gateway = new AzureModelGateway(Settings(), true);
        Assert.Equal(.00875m, gateway.EstimateUsd(3000, 1000));
        Assert.Null(gateway.EstimateUsd(null, 1000));
        Assert.Null(new AzureModelGateway(new(), true).EstimateUsd(1000, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => gateway.EstimateUsd(-1, 0));
    }
}
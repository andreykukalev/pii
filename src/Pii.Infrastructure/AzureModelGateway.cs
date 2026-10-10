using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;
using Pii.Application;

namespace Pii.Infrastructure;

public sealed class ModelGatewayException(string code, int? httpStatus = null) : Exception(code)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
}

public sealed class AzureModelOptions
{
    public ExecutionMode Mode { get; init; } = ExecutionMode.Live;
    public bool Authorized { get; init; }
    public bool ProcessingAuthorized { get; init; }
    public string? Endpoint { get; init; }
    public string? Deployment { get; init; }
    public string? ModelVersion { get; init; }
    public string ApiVersion { get; init; } = "2024-10-21";
    public string? RateCardVersion { get; init; }
    public string? ProcessingRegion { get; init; }
    public decimal? InputUsdPerMillion { get; init; }
    public decimal? OutputUsdPerMillion { get; init; }
    public bool SupportsStrictSchemaAndTools { get; init; }
    public string Authentication { get; init; } = "DefaultAzureCredential";
    public string? ApiKey { get; init; }
    public RuntimeLimits Limits { get; init; } = new();

    public string? Diagnostic(bool development, bool checkCredentials = true)
    {
        if (!Authorized) return "ModelDisabled: Pii:LiveAuthorized must be true.";
        if (!ProcessingAuthorized || string.IsNullOrWhiteSpace(ProcessingRegion)) return "ModelProcessingRightsOrRegionMissing";
        if (InputUsdPerMillion is not > 0 || OutputUsdPerMillion is not > 0 || string.IsNullOrWhiteSpace(RateCardVersion)) return "RequiredInputOutputRatesPerMillionMissing";
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
            || !uri.Host.EndsWith(".openai.azure.com", StringComparison.Ordinal) || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return "AzureOpenAIResourceRootEndpointRequired";
        if (string.IsNullOrWhiteSpace(Deployment) || Deployment.Length > 128 || !Deployment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return "AzureOpenAIDeploymentRequired";
        if (string.IsNullOrWhiteSpace(ModelVersion) || !SupportsStrictSchemaAndTools || ApiVersion != "2024-10-21") return "ConfiguredModelVersionStrictToolsOrSupportedApiBindingMissing";
        if (Authentication == "ApiKey") return development && (!checkCredentials || !string.IsNullOrWhiteSpace(ApiKey)) ? null : "ApiKeyAllowedOnlyInExplicitLocalDevelopment";
        return Authentication == "DefaultAzureCredential" ? null : "UnsupportedModelAuthentication";
    }
}

public sealed class AzureModelGateway(AzureModelOptions settings, bool development, HttpClient? recordedTransport = null) : IModelGateway
{
    public decimal? EstimateUsd(long? inputTokens, long? outputTokens)
    {
        if (inputTokens is null || outputTokens is null || settings.InputUsdPerMillion is null || settings.OutputUsdPerMillion is null) return null;
        if (inputTokens < 0 || outputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens));
        return (inputTokens.Value * settings.InputUsdPerMillion.Value + outputTokens.Value * settings.OutputUsdPerMillion.Value) / 1_000_000m;
    }

    public async Task<ModelReply> CompleteAsync(ModelRequest request, ExecutionMode mode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (mode != settings.Mode || mode == ExecutionMode.FixtureReplay || mode == ExecutionMode.ModelOnFixtures && !development) throw new ModelGatewayException("ModelModeForbidden");
        if (settings.Diagnostic(development) is { } diagnostic) throw new ModelGatewayException(diagnostic);
        if (request.MaximumOutputTokens <= 0 || request.MaximumOutputTokens > settings.Limits.MaximumOutputTokens
            || JsonDefaults.Serialize(request).Length > settings.Limits.MaximumInputCharacters || request.Tools.Count > 16)
            throw new ModelGatewayException("ModelRequestBoundsExceeded");
        RequireObjectSchema(request.OutputSchema);
        var messages = TranslateMessages(request.Messages);
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaximumOutputTokens,
            AllowParallelToolCalls = false,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("pii_output_v1", BinaryData.FromString(request.OutputSchema), jsonSchemaIsStrict: true)
        };
        foreach (var descriptor in request.Tools)
        {
            RequireObjectSchema(descriptor.InputSchema);
            options.Tools.Add(ChatTool.CreateFunctionTool(descriptor.Name, descriptor.Description, BinaryData.FromString(descriptor.InputSchema), functionSchemaIsStrict: true));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var clientTransport = recordedTransport is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) } : null;
        var clientOptions = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2024_10_21)
        {
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            Transport = new HttpClientPipelineTransport(recordedTransport ?? clientTransport!),
            NetworkTimeout = TimeSpan.FromSeconds(60)
        };
        var azure = settings.Authentication == "ApiKey"
            ? new AzureOpenAIClient(new Uri(settings.Endpoint!), new ApiKeyCredential(settings.ApiKey!), clientOptions)
            : new AzureOpenAIClient(new Uri(settings.Endpoint!), new DefaultAzureCredential(), clientOptions);
        var chat = azure.GetChatClient(settings.Deployment!);
        try
        {
            var response = await chat.CompleteChatAsync(messages, options, deadline.Token);
            var completion = response.Value;
            if (!string.IsNullOrEmpty(completion.Refusal)) throw new ModelGatewayException("ModelRefusal");
            if (completion.FinishReason == ChatFinishReason.ContentFilter) throw new ModelGatewayException("ModelContentFiltered");
            if (completion.FinishReason == ChatFinishReason.Length) throw new ModelGatewayException("ModelOutputTruncated");
            if (completion.FinishReason != ChatFinishReason.Stop && completion.FinishReason != ChatFinishReason.ToolCalls) throw new ModelGatewayException("UnsupportedModelFinishReason");
            if (completion.ToolCalls.Count > 1) throw new ModelGatewayException("ParallelToolCallsRejected");
            var calls = completion.ToolCalls.Select(call => new ModelToolCall(call.Id, call.FunctionName, call.FunctionArguments.ToString())).ToList();
            foreach (var call in calls)
            {
                if (string.IsNullOrWhiteSpace(call.Id) || !request.Tools.Any(tool => tool.Name == call.Name)) throw new ModelGatewayException("UnknownModelToolCall");
                using var arguments = JsonDocument.Parse(call.Arguments);
                if (arguments.RootElement.ValueKind != JsonValueKind.Object) throw new ModelGatewayException("InvalidToolArguments");
            }
            var content = string.Concat(completion.Content.Select(part => part.Text));
            if (calls.Count == 0)
            {
                using var output = JsonDocument.Parse(content);
                if (output.RootElement.ValueKind != JsonValueKind.Object) throw new ModelGatewayException("InvalidStructuredModelOutput");
            }
            using var raw = JsonDocument.Parse(response.GetRawResponse().Content);
            long? input = null, outputTokens = null;
            if (raw.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt64(out var promptTokens)) input = promptTokens;
                if (usage.TryGetProperty("completion_tokens", out var output) && output.TryGetInt64(out var tokens)) outputTokens = tokens;
            }
            var headers = response.GetRawResponse().Headers;
            string? requestId = headers.TryGetValue("apim-request-id", out var azureRequestId) ? azureRequestId
                : headers.TryGetValue("x-request-id", out var providerRequestId) ? providerRequestId : null;
            return new(content, calls, input, outputTokens, requestId);
        }
        catch (ClientResultException error)
        {
            throw new ModelGatewayException(error.Status switch { 401 or 403 => "ModelAuthenticationDenied", 404 => "ModelDeploymentNotFound", 429 => "ModelRateLimitedNoAutomaticRetry", 400 => "ModelRequestOrContentFilterRejected", _ => "ModelProviderFailureUnknownCharge" }, error.Status);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new ModelGatewayException("ModelTimeoutUnknownCharge"); }
        catch (JsonException) { throw new ModelGatewayException("MalformedModelJson"); }
        catch (HttpRequestException) { throw new ModelGatewayException("ModelTransportFailureUnknownCharge"); }
    }

    public static List<ChatMessage> TranslateMessages(List<ModelMessage> source)
    {
        if (source.Count is 0 or > 100) throw new ModelGatewayException("InvalidModelMessageCount");
        var messages = new List<ChatMessage>();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in source)
        {
            if (message.Role != "tool" && pending.Count != 0) throw new ModelGatewayException("UnresolvedToolCallHistory");
            switch (message.Role)
            {
                case "system": messages.Add(new SystemChatMessage(message.Content)); break;
                case "user": messages.Add(new UserChatMessage(message.Content)); break;
                case "assistant":
                    var calls = message.ToolCalls ?? [];
                    if (calls.Count > 1) throw new ModelGatewayException("ParallelToolHistoryRejected");
                    foreach (var call in calls)
                        if (string.IsNullOrWhiteSpace(call.Id) || !seen.Add(call.Id) || !pending.Add(call.Id)) throw new ModelGatewayException("DuplicateToolCallId");
                    var assistant = calls.Count == 0 ? new AssistantChatMessage(message.Content)
                        : new AssistantChatMessage(calls.Select(call => ChatToolCall.CreateFunctionToolCall(call.Id, call.Name, BinaryData.FromString(call.Arguments))));
                    if (calls.Count > 0 && message.Content.Length > 0) assistant.Content.Add(ChatMessageContentPart.CreateTextPart(message.Content));
                    messages.Add(assistant);
                    break;
                case "tool":
                    if (message.ToolCallId is null || !pending.Remove(message.ToolCallId)) throw new ModelGatewayException("MismatchedToolCallId");
                    messages.Add(new ToolChatMessage(message.ToolCallId, message.Content));
                    break;
                default: throw new ModelGatewayException("UnsupportedModelMessageRole");
            }
        }
        if (pending.Count != 0) throw new ModelGatewayException("UnresolvedToolCallHistory");
        return messages;
    }

    private static void RequireObjectSchema(string schema)
    {
        try
        {
            using var document = JsonDocument.Parse(schema);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "object" || root.GetProperty("additionalProperties").ValueKind != JsonValueKind.False
                || root.GetProperty("required").ValueKind != JsonValueKind.Array || root.GetProperty("properties").ValueKind != JsonValueKind.Object)
                throw new ModelGatewayException("StrictObjectJsonSchemaRequired");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { throw new ModelGatewayException("StrictObjectJsonSchemaRequired"); }
    }
}
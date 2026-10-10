using System.Text.Json;
using Pii.Domain;

namespace Pii.Application;

public sealed class FixtureModelGateway : IModelGateway
{
    public decimal? EstimateUsd(long? inputTokens, long? outputTokens) => null;
    public Task<ModelReply> CompleteAsync(ModelRequest request, ExecutionMode mode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (mode != ExecutionMode.FixtureReplay) throw new InvalidOperationException("Fixture transport is never a live-model fallback.");
        using var context = JsonDocument.Parse(request.Messages.First(item => item.Role == "user").Content);
        var root = context.RootElement;
        var role = Enum.Parse<AgentRole>(root.GetProperty("role").GetString()!);
        var instrumentId = root.GetProperty("taskInstrumentId").GetString()!;
        var catalog = JsonDefaults.Parse<List<Instrument>>(root.GetProperty("catalog").GetRawText());
        var held = JsonDefaults.Parse<List<string>>(root.GetProperty("heldIds").GetRawText());
        var results = root.GetProperty("results").EnumerateArray().Select(item => new Result(
            Enum.Parse<AgentRole>(item.GetProperty("role").GetString()!), item.GetProperty("instrumentId").GetString()!,
            item.GetProperty("intent").GetString()!, JsonDefaults.Parse<AgentOutput>(item.GetProperty("output").GetRawText()))).ToList();
        foreach (var message in request.Messages.Where(item => item.Role == "tool"))
        {
            using var payload = JsonDocument.Parse(message.Content);
            if (payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("role", out var delegatedRole))
            {
                var previous = request.Messages.TakeWhile(item => item != message).Last(item => item.Role == "assistant");
                using var arguments = JsonDocument.Parse(previous.ToolCalls![0].Arguments);
                results.Add(new(Enum.Parse<AgentRole>(delegatedRole.GetString()!), payload.RootElement.GetProperty("instrumentId").GetString()!,
                    arguments.RootElement.GetProperty("question").GetString()!, JsonDefaults.Parse<AgentOutput>(payload.RootElement.GetProperty("output").GetRawText())));
            }
        }
        if (role == AgentRole.InvestmentOrchestrator)
        {
            Result? critic = results.LastOrDefault(item => item.Role == AgentRole.ResearchCritic);
            if (!results.Any(item => item.Role == AgentRole.MarketSectorAnalyst)) return Tool("DelegateMarketSectorAnalyst", "", "Research sector context and outside alternatives");
            var heldId = held.FirstOrDefault() ?? throw new InvalidDataException("Fixture has no holding.");
            if (!results.Any(item => item.Role == AgentRole.CompanyEtfResearchAnalyst && item.Instrument == heldId))
                return Tool("DelegateCompanyEtfResearchAnalyst", heldId, "Assess held thesis across both horizons");
            var outside = catalog.FirstOrDefault(item => !held.Contains(item.Id)) ?? throw new InvalidDataException("Fixture has no outside candidate.");
            if (!results.Any(item => item.Role == AgentRole.CompanyEtfResearchAnalyst && item.Instrument == outside.Id))
                return Tool("DelegateCompanyEtfResearchAnalyst", outside.Id, "Assess outside opportunity across both horizons");
            if (critic is null) return Tool("DelegateResearchCritic", "", "Challenge factual support and opposing theses");
            foreach (var follow in critic.Output.FollowUps)
                if (!results.Any(item => item.Role == AgentRole.CompanyEtfResearchAnalyst && item.Instrument == follow.InstrumentId && item.Intent == follow.Question))
                    return Tool("DelegateCompanyEtfResearchAnalyst", follow.InstrumentId, follow.Question);
            if (critic.Output.FollowUps.Count > 0 && results.Last().Role != AgentRole.ResearchCritic)
                return Tool("DelegateResearchCritic", "", "Recheck follow-up evidence and unresolved uncertainty");
            return Reply(JsonDefaults.Serialize(new OrchestratorDecision("ProposeFinish", "", "", "", "Required held, sector, outside and critic stages executed; preserve uncertainty and apply deterministic gates.")));
        }
        if (role != AgentRole.InvestmentSupervisor && !request.Messages.Any(item => item.Role == "tool"))
            return Tool(role == AgentRole.CompanyEtfResearchAnalyst ? "GetFinancialFacts" : "ReadEvidence", instrumentId, "Read permitted evidence for the assigned scope");
        var evidence = JsonDefaults.Parse<List<EvidenceRecord>>(root.GetProperty("evidence").GetRawText());
        if (role == AgentRole.ResearchCritic)
        {
            var hasFollowUp = results.Any(item => item.Intent.StartsWith("Verify", StringComparison.Ordinal));
            var uncertain = evidence.FirstOrDefault(item => item.Text.Contains("uncertain", StringComparison.OrdinalIgnoreCase) || item.Text.Contains("contradiction", StringComparison.OrdinalIgnoreCase));
            return Reply(JsonDefaults.Serialize(new AgentOutput([], [], uncertain is not null && !hasFollowUp
                ? [new(uncertain.InstrumentId, "Verify demand counterargument with available evidence", "Announcement alone does not establish realized demand")] : [],
                ["Citation existence is not semantic proof; future demand remains a hypothesis, source coverage is limited."])));
        }
        if (role == AgentRole.InvestmentSupervisor)
            return Reply(JsonDefaults.Serialize(new AgentOutput([], [], [], ["Investment policy is unconfigured; no sized actions or suitability approval. Research remains informational."])));
        var selectedId = instrumentId.Length == 0 ? catalog[0].Id : instrumentId;
        var support = evidence.Where(item => item.InstrumentId == selectedId).Select(item => item.Id).Take(3).ToList();
        var claims = Enum.GetValues<Horizon>().Select(horizon => new Claim($"{horizon}-thesis", selectedId, horizon, ClaimKind.Interpretation,
            horizon == Horizon.LongTerm ? "Disclosed business activity supports a thesis to monitor, subject to demand and execution uncertainty." : "Tactical entry depends on fresh price and catalyst evidence; no profitable outcome is established.", support, null, [])).ToList();
        var assessments = role == AgentRole.CompanyEtfResearchAnalyst ? claims.Select(claim => new Assessment(selectedId, claim.Horizon,
            held.Contains(selectedId) ? RecommendationCategory.HOLD : RecommendationCategory.WATCHLIST, claim.Text,
            "Expansion may not translate into durable demand; limited evidence does not establish valuation or suitability.", [claim.Id],
            ["Execution and source-coverage risk", "No investment sizing or complete earnings-calendar evidence"])).ToList() : [];
        return Reply(JsonDefaults.Serialize(new AgentOutput(claims, assessments, [], ["Explicit fixture transport: assertions exercise application wiring, not investment analysis by a live model."])));
    }
    private sealed record Result(AgentRole Role, string Instrument, string Intent, AgentOutput Output);
    private static Task<ModelReply> Reply(string content) => Task.FromResult(new ModelReply(content, [], null, null, null));
    private static Task<ModelReply> Tool(string name, string instrument, string question) => Task.FromResult(new ModelReply("",
        [new(Guid.NewGuid().ToString("N"), name, JsonDefaults.Serialize(new { instrumentId = instrument, question }))], null, null, null));
}
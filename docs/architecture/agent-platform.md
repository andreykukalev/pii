# Agent Platform

The [application pipeline](../../src/Pii.Application/InvestmentPipeline.cs) implements the actual multi-turn model/tool loop in the shared finite worker. It is not a Foundry hosted-agent deployment, vector store, prompt-only scaffold or one-call summary.

| Logical role | Executable responsibility / boundary |
| --- | --- |
| InvestmentOrchestrator | Model selects permitted delegation, order/questions and continuation; runtime requires minimum held/sector/outside/critic stages |
| PortfolioAnalyst | Deterministic portfolio facts from pinned confirmed revision and supplied quotes; no model arithmetic |
| MarketSectorAnalyst | Evidence-qualified sector/market/outside research using permitted primitive tools |
| CompanyEtfResearchAnalyst | Held/outside instrument research with kind-appropriate criteria, two horizons, thesis/counterargument/risks |
| ResearchCritic | Distinct model context challenges evidence/claims; bounded follow-ups, no recursive specialist delegation |
| InvestmentSupervisor | Deterministic eligibility/policy gates plus constrained explanation; model cannot approve suitability or publish |

RunCoordinator owns durable acceptance; SQL store owns claims/fencing/publication. These are not seventh/eighth research agents. [Contracts](../../src/Pii.Application/Contracts.cs) include AgentTaskRequest/Result, AgentOutput, Claim, OrchestratorDecision, ToolDescriptor, ModelMessage/ToolCall/Reply, TraceRecord, UsageRecord and AnalysisRun. Server-owned run/revision/deadline/permission context is checked before accepting results. Not every richer conceptual-plan field exists in the implemented records.

Orchestrator tool descriptors delegate MarketSector, CompanyEtf and Critic; research tools are GetFinancialFacts, GetNewsEvents, GetMarketObservations, ReadEvidence, ReadPriorAssessment and GetExposure. Supervisor has no tools. Unknown functions, invalid schemas, cross-scope identities, invalid citations/numeric references, future/unauthorized evidence and repeated no-progress work are rejected. The implemented registry is authoritative; do not advertise arbitrary URL browsing, shell/SQL, trade, policy-write, broker or remote MCP tools.

Default engineering ceilings from [RuntimeLimits](../../src/Pii.Application/Contracts.cs): 12 decisions, 5 research tasks, 2 critic passes, 32 tool invocations, 24 model submissions, 10 minutes, 2,500 output tokens/call, 40,000 input characters, 350,000 reserved run tokens and 12,000 reserved call tokens. Default USD1/run, USD20/month and USD0.15 maximum-call reservation are safety configuration, **not verified cost, a sufficient minimal-loop budget, or investment limits**. Count continuations, schema failures, critique, supervisor, retries and uncertain charges. Recovery must not reset counters. Platform retries are zero; one worker execution processes at most one run in the production template.

[AzureModelGateway](../../src/Pii.Infrastructure/AzureModelGateway.cs) uses released Azure.AI.OpenAI 2.1.0, Chat Completions API `2024-10-21`, strict JSON-schema outputs/tools and `parallel_tool_calls=false`, matched tool-result IDs, a 60-second call timeout and zero SDK retries. API support alone does not prove a selected model/version supports these features or token settings. Refusal, filter, truncation, auth/deployment denial, 429, timeout, malformed JSON and unknown-charge transport failures remain distinct. Input/output rates must be positive per-million USD with an approved rate-card version; cached/reasoning-specific pricing is not independently represented by the current estimator.

Only permitted minimized facts/evidence enter model context, never original CSV/PDF bytes or hidden reasoning. Persist supplied messages/outputs where lawful for inspection, not a claim of access to chain-of-thought. Schema and citation checks are not proof of semantic truth; independent critic and user review remain necessary.

Modes: FixtureReplay substitutes only external transports in explicit Development; ModelOnFixtures makes a real model call on permitted labeled fixture evidence in Development; Live requires approved model/source access and real verified inputs. Production rejects both fixture modes and never rebinds live errors to fixtures. No live mode has been exercised here.

**Source-owner blocker:** web and worker intentionally do not share model/provider environment settings, but accepted runs currently carry the submitter's `ModelBinding` and execution rejects binding differences. Worker-only configuration can invalidate web-submitted runs. Resolve this in source with an explicit non-secret run/config contract and regression test, not by giving the web model credentials/permissions. See [acceptance](../product/roadmap-and-acceptance.md).
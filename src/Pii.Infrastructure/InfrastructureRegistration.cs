using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pii.Application;
using Pii.Domain;

namespace Pii.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddPiiInfrastructure(this IServiceCollection services, IConfiguration configuration, bool development, bool worker = false)
    {
        var runtime = ReadRuntimeConfiguration(configuration, development);
        services.AddSingleton(runtime);
        var connection = configuration["Pii:Sql:ConnectionString"] ?? configuration.GetConnectionString("Pii");
        if (string.IsNullOrWhiteSpace(connection)) connection = development
            ? "Server=(localdb)\\MSSQLLocalDB;Database=PiiDevelopment;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=15"
            : throw new InvalidOperationException("Pii:Sql:ConnectionString is required for Azure SQL; no file/SQLite fallback.");
        if (!development)
        {
            var sql = new SqlConnectionStringBuilder(connection);
            var host = sql.DataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? sql.DataSource[4..] : sql.DataSource;
            host = host.Split(',')[0];
            if (!host.EndsWith(".database.windows.net", StringComparison.OrdinalIgnoreCase) || sql.TrustServerCertificate || sql.Encrypt == SqlConnectionEncryptOption.Optional)
                throw new InvalidOperationException("Production requires encrypted Azure SQL with verified server certificate.");
        }
        var containerName = configuration["Pii:Blob:Container"] ?? "pii-documents";
        var blobUri = development ? null : RequireAzureRoot(configuration["Pii:Blob:ServiceUri"], ".blob.core.windows.net");
        services.AddDbContextFactory<WorkspaceDbContext>(options => options.UseSqlServer(connection, sql => sql.CommandTimeout(20)));
        services.AddSingleton<IWorkspaceStore, SqlWorkspaceStore>();
        if (worker) services.AddSingleton<ISourceCreditStore, SqlSourceCreditStore>();
        services.AddSingleton<IWorkDispatch, DatabasePollingDispatch>();
        services.AddSingleton<IArtifactStore>(_ =>
        {
            if (development)
            {
                var storage = configuration["Pii:Blob:ConnectionString"] ?? "UseDevelopmentStorage=true";
                return new BlobArtifactStore(new BlobServiceClient(storage).GetBlobContainerClient(containerName), true);
            }
            return new BlobArtifactStore(new BlobServiceClient(blobUri!, new DefaultAzureCredential()).GetBlobContainerClient(containerName));
        });
        services.AddSingleton<IResearchSources>(provider =>
        {
            var options = ReadResearchSourceOptions(configuration, worker, runtime.LiveAuthorized);
            return new ResearchSources(ResearchHttp.CreateClient(), options, provider.GetRequiredService<RuntimeConfiguration>(), development,
                worker ? provider.GetRequiredService<ISourceCreditStore>() : null);
        });
        if (worker)
        {
            if (runtime.Mode == ExecutionMode.FixtureReplay)
            {
                services.AddSingleton<IModelGateway, FixtureModelGateway>();
            }
            else services.AddSingleton<IModelGateway>(_ => new AzureModelGateway(ReadModelOptions(configuration, runtime.Limits, runtime.Mode,
                Environment.GetEnvironmentVariable("PII_AZURE_OPENAI_API_KEY")), development));
        }
        return services;
    }

    public static ResearchSourceOptions ReadResearchSourceOptions(IConfiguration configuration, bool worker, bool liveAuthorized) => new()
    {
        Authorized = worker && liveAuthorized,
        SecRights = worker && configuration.GetValue<bool>("Pii:Sources:Sec:RightsAuthorized"),
        SecModelProcessingAllowed = worker && configuration.GetValue<bool>("Pii:Sources:Sec:ModelProcessingAllowed"),
        SecUserAgent = worker ? configuration["Pii:Sources:Sec:UserAgent"] : null,
        SecCompanyFactsMaximumBytes = configuration.GetValue<int?>("Pii:Sources:Sec:CompanyFactsMaximumBytes") ?? ResearchHttp.MaximumBytes,
        SecCompanyFactsLargerResponseAuthorized = worker && configuration.GetValue<bool>("Pii:Sources:Sec:CompanyFactsLargerResponseAuthorized"),
        EcbRights = worker && configuration.GetValue<bool>("Pii:Sources:Ecb:RightsAuthorized"),
        EcbModelProcessingAllowed = worker && configuration.GetValue<bool>("Pii:Sources:Ecb:ModelProcessingAllowed"),
        TwelveDataRights = worker && configuration.GetValue<bool>("Pii:Sources:TwelveData:RightsAuthorized"),
        TwelveDataEvidenceRetentionAuthorized = worker && configuration.GetValue<bool>("Pii:Sources:TwelveData:EvidenceRetentionAuthorized"),
        TwelveDataModelProcessingAllowed = worker && configuration.GetValue<bool>("Pii:Sources:TwelveData:ModelProcessingAllowed"),
        TwelveDataApiKey = worker ? Environment.GetEnvironmentVariable("PII_TWELVEDATA_API_KEY") : null,
        TwelveDataEntitlements = worker ? (configuration.GetSection("Pii:Sources:TwelveData:Entitlements").Get<string[]>() ?? []).ToHashSet(StringComparer.Ordinal) : [],
        TwelveDataRequestCredits = worker ? configuration.GetSection("Pii:Sources:TwelveData:RequestCreditAuthorization").Get<RequestCreditAuthorization>() ?? new() : new(),
        ExchangeTimeZones = configuration.GetSection("Pii:Sources:ExchangeTimeZones").Get<Dictionary<string, string>>() ?? []
    };

    public static RuntimeConfiguration ReadRuntimeConfiguration(IConfiguration configuration, bool development)
    {
        var modeText = configuration["Pii:Mode"] ?? (development ? "FixtureReplay" : "Live");
        if (!Enum.TryParse<ExecutionMode>(modeText, false, out var mode) || !Enum.IsDefined(mode) || modeText != mode.ToString()) throw new InvalidOperationException("Pii:Mode must be FixtureReplay, ModelOnFixtures, or Live.");
        if (!development && mode != ExecutionMode.Live) throw new InvalidOperationException("FixtureReplay and ModelOnFixtures are forbidden in production.");
        var limits = configuration.GetSection("Pii:Limits").Get<RuntimeLimits>() ?? new();
        if (limits.MaximumRunMinutes is <= 0 or > 15 || limits.MaximumDecisions <= 0 || limits.MaximumResearchTasks <= 0 || limits.MaximumTools <= 0
            || limits.MaximumModelSubmissions <= 0 || limits.MaximumCriticPasses <= 0 || limits.MaximumOutputTokens <= 0 || limits.MaximumInputCharacters <= 0
            || limits.MonthlyAiUsd < 0 || limits.PerRunUsd < 0 || limits.MaximumCallUsd < 0 || limits.MaximumRunTokens <= 0 || limits.MaximumCallTokens <= 0)
            throw new InvalidOperationException("Invalid Pii:Limits bounds.");
        var catalog = mode == ExecutionMode.Live ? configuration.GetSection("Pii:CandidateCatalog").Get<List<Instrument>>() ?? [] : SyntheticResearchFixture.Catalog;
        if (catalog.Count > 32 || catalog.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Count) throw new InvalidOperationException("Candidate catalog is bounded to 32 unique instruments.");
        foreach (var instrument in catalog) ResearchHttp.ValidateInstrument(instrument);
        if (mode == ExecutionMode.Live && catalog.Any(item => item.Symbol is "SAMPLE" or "OTHER" || item.Sector.StartsWith("Synthetic", StringComparison.Ordinal)))
            throw new InvalidOperationException("Synthetic candidate identifiers are forbidden in Live mode.");
        var model = ReadModelOptions(configuration, limits, mode, null);
        var modelDiagnostic = model.Diagnostic(development, checkCredentials: false);
        var diagnostic = mode == ExecutionMode.FixtureReplay ? "Static dated synthetic Development fixtures; no inference or provider calls."
            : modelDiagnostic ?? "Model configured; credentials are checked only by the worker; sources require separate rights/entitlements. Live verification NotRun.";
        var authorized = mode != ExecutionMode.FixtureReplay && modelDiagnostic is null;
        var binding = mode == ExecutionMode.FixtureReplay ? "SyntheticFixtureReplay-v1" : $"AzureOpenAI/{model.Deployment ?? "Unconfigured"}/{model.ModelVersion ?? "Unknown"}/api={model.ApiVersion}/rates={model.RateCardVersion ?? "Missing"}";
        return new(mode, limits, catalog, binding, authorized, diagnostic, configuration.GetSection("Pii:ExcludedInstrumentIds").Get<List<string>>());
    }

    private static AzureModelOptions ReadModelOptions(IConfiguration configuration, RuntimeLimits limits, ExecutionMode mode, string? apiKey) => new()
    {
        Mode = mode,
        Authorized = configuration.GetValue<bool>("Pii:LiveAuthorized"), ProcessingAuthorized = configuration.GetValue<bool>("Pii:Model:ProcessingAuthorized"),
        Endpoint = configuration["Pii:Model:Endpoint"], Deployment = configuration["Pii:Model:Deployment"], ModelVersion = configuration["Pii:Model:ModelVersion"],
        ApiVersion = configuration["Pii:Model:ApiVersion"] ?? "2024-10-21", RateCardVersion = configuration["Pii:Model:RateCardVersion"],
        ProcessingRegion = configuration["Pii:Model:ProcessingRegion"], InputUsdPerMillion = configuration.GetValue<decimal?>("Pii:Model:InputUsdPerMillion"),
        OutputUsdPerMillion = configuration.GetValue<decimal?>("Pii:Model:OutputUsdPerMillion"),
        SupportsStrictSchemaAndTools = configuration.GetValue<bool>("Pii:Model:SupportsStrictSchemaAndTools"),
        Authentication = configuration["Pii:Model:Authentication"] ?? "DefaultAzureCredential", ApiKey = apiKey, Limits = limits
    };

    private static Uri RequireAzureRoot(string? value, string suffix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || !uri.Host.EndsWith(suffix, StringComparison.Ordinal)
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Azure resource HTTPS root URI is required.");
        return uri;
    }
}
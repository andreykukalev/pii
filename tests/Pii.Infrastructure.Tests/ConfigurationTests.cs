using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pii.Application;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Infrastructure.Tests;

public sealed class ConfigurationTests
{
    private static IConfiguration Configuration(Dictionary<string, string?>? values = null) => new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();

    [Fact]
    public void DevelopmentAcceptsExplicitAzureBlobWithoutOpeningNetwork()
    {
        using var provider = new ServiceCollection().AddPiiInfrastructure(Configuration(new()
        {
            ["Pii:Blob:ServiceUri"] = "https://recorded.blob.core.windows.net/"
        }), true).BuildServiceProvider();
        Assert.IsType<BlobArtifactStore>(provider.GetRequiredService<IArtifactStore>());
    }

    [Theory]
    [InlineData("http://recorded.blob.core.windows.net/")]
    [InlineData("https://attacker.test/")]
    [InlineData("https://recorded.blob.core.windows.net/container")]
    public void DevelopmentDoesNotFallbackForInvalidAzureBlob(string uri) =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPiiInfrastructure(
            Configuration(new() { ["Pii:Blob:ServiceUri"] = uri }), true));

    [Fact]
    public void AmbiguousBlobAuthenticationFailsBeforeRegistration() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPiiInfrastructure(Configuration(new()
        {
            ["Pii:Blob:ServiceUri"] = "https://recorded.blob.core.windows.net/",
            ["Pii:Blob:ConnectionString"] = "UseDevelopmentStorage=true"
        }), true));

    [Fact]
    public void OnlyWorkerReadsConfiguredProviderKey()
    {
        var configuration = Configuration(new() { ["PII_TWELVEDATA_API_KEY"] = "synthetic-local-secret" });
        Assert.Equal("synthetic-local-secret", InfrastructureRegistration.ReadResearchSourceOptions(configuration, true, false).TwelveDataApiKey);
        Assert.Null(InfrastructureRegistration.ReadResearchSourceOptions(configuration, false, false).TwelveDataApiKey);
    }

    [Fact]
    public void DevelopmentDefaultsHaveOutsideCandidateButNoSeededHoldingsOrWebModel()
    {
        var services = new ServiceCollection().AddPiiInfrastructure(Configuration(), true);
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<RuntimeConfiguration>();
        Assert.Equal(ExecutionMode.FixtureReplay, runtime.Mode);
        Assert.Equal(2, runtime.CandidateCatalog.Count);
        Assert.Contains(runtime.CandidateCatalog, item => item.Symbol == "OTHER");
        Assert.Null(provider.GetService<IModelGateway>());
        Assert.Null(provider.GetService<ISourceCreditStore>());
        Assert.IsType<SqlWorkspaceStore>(provider.GetRequiredService<IWorkspaceStore>());
        using var database = provider.GetRequiredService<IDbContextFactory<WorkspaceDbContext>>().CreateDbContext();
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", database.Database.ProviderName);
        Assert.Contains("(localdb)", database.Database.GetConnectionString());
        Assert.NotNull(database.Model.FindEntityType(typeof(SourceCreditDayRow)));
        Assert.NotNull(database.Model.FindEntityType(typeof(SourceCreditEntryRow)));
    }

    [Fact]
    public void WorkerRegistersSqlCreditStoreWithoutOpeningDatabase()
    {
        using var provider = new ServiceCollection().AddPiiInfrastructure(Configuration(), true, true).BuildServiceProvider();
        Assert.IsType<SqlSourceCreditStore>(provider.GetRequiredService<ISourceCreditStore>());
        Assert.IsType<ResearchSources>(provider.GetRequiredService<IResearchSources>());
    }

    [Theory]
    [InlineData("FixtureReplay")]
    [InlineData("ModelOnFixtures")]
    [InlineData("not-a-mode")]
    [InlineData("1")]
    public void ProductionRejectsFixturesAndUnknownModes(string mode) => Assert.Throws<InvalidOperationException>(() =>
        InfrastructureRegistration.ReadRuntimeConfiguration(Configuration(new() { ["Pii:Mode"] = mode }), false));

    [Fact]
    public void ProductionRequiresSqlAndNeverFallsBackToFiles() => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPiiInfrastructure(Configuration(), false));

    [Fact]
    public void MissingRatesKeepPaidModesDisabledWithoutLeakingConfiguredSecrets()
    {
        var configuration = Configuration(new() { ["Pii:Mode"] = "ModelOnFixtures", ["Pii:LiveAuthorized"] = "true", ["Pii:Model:ApiKey"] = "secret-never-consumed" });
        var runtime = InfrastructureRegistration.ReadRuntimeConfiguration(configuration, true);
        Assert.False(runtime.LiveAuthorized);
        Assert.DoesNotContain("secret-never-consumed", JsonDefaults.Serialize(runtime));
        Assert.Contains("Missing", runtime.ModelBinding);
        using var provider = new ServiceCollection().AddPiiInfrastructure(configuration, true, true).BuildServiceProvider();
        Assert.IsType<AzureModelGateway>(provider.GetRequiredService<IModelGateway>());
    }

    [Fact]
    public void NonDevelopmentRejectsSharedKeyInferenceAuthentication()
    {
        var options = new AzureModelOptions { Authorized = true, ProcessingAuthorized = true, ProcessingRegion = "region", InputUsdPerMillion = 1, OutputUsdPerMillion = 2,
            RateCardVersion = "test", Endpoint = "https://recorded.openai.azure.com/", Deployment = "test", ModelVersion = "test", SupportsStrictSchemaAndTools = true,
            Authentication = "ApiKey", ApiKey = "synthetic-test" };
        Assert.Equal("ApiKeyAllowedOnlyInExplicitLocalDevelopment", options.Diagnostic(false));
        Assert.Null(options.Diagnostic(true));
    }

    [Theory]
    [InlineData("https://attacker.test/")]
    [InlineData("http://recorded.openai.azure.com/")]
    [InlineData("https://recorded.openai.azure.com/openai/v1/")]
    [InlineData("https://recorded.openai.azure.com/?token=secret")]
    public void ModelEndpointMustBeExplicitAzureResourceRoot(string endpoint)
    {
        var options = new AzureModelOptions { Authorized = true, ProcessingAuthorized = true, ProcessingRegion = "region", InputUsdPerMillion = 1, OutputUsdPerMillion = 2, RateCardVersion = "test", Endpoint = endpoint };
        Assert.Equal("AzureOpenAIResourceRootEndpointRequired", options.Diagnostic(true));
    }

    [Fact]
    public void InvalidRuntimeLimitsFailBeforeServiceRegistration() => Assert.Throws<InvalidOperationException>(() =>
        InfrastructureRegistration.ReadRuntimeConfiguration(Configuration(new() { ["Pii:Limits:MaximumRunMinutes"] = "0" }), true));

    [Fact]
    public void RatesAloneCannotAuthorizeAnInvalidModelBinding()
    {
        var runtime = InfrastructureRegistration.ReadRuntimeConfiguration(Configuration(new()
        {
            ["Pii:Mode"] = "Live", ["Pii:LiveAuthorized"] = "true", ["Pii:Model:ProcessingAuthorized"] = "true", ["Pii:Model:ProcessingRegion"] = "recorded-region",
            ["Pii:Model:InputUsdPerMillion"] = "1", ["Pii:Model:OutputUsdPerMillion"] = "2", ["Pii:Model:RateCardVersion"] = "recorded-only",
            ["Pii:Model:Endpoint"] = "https://attacker.test/", ["Pii:Model:Deployment"] = "recorded", ["Pii:Model:ModelVersion"] = "recorded",
            ["Pii:Model:SupportsStrictSchemaAndTools"] = "true"
        }), true);
        Assert.False(runtime.LiveAuthorized);
        Assert.Equal("AzureOpenAIResourceRootEndpointRequired", runtime.Diagnostic);
    }

    [Theory]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=test;Integrated Security=true")]
    [InlineData("Server=tcp:recorded.database.windows.net,1433;Database=test;Encrypt=false")]
    [InlineData("Server=tcp:recorded.database.windows.net,1433;Database=test;TrustServerCertificate=true")]
    public void ProductionRejectsLocalOrInsecureSql(string connection) => Assert.Throws<InvalidOperationException>(() =>
        new ServiceCollection().AddPiiInfrastructure(Configuration(new() { ["Pii:Sql:ConnectionString"] = connection, ["Pii:Blob:ServiceUri"] = "https://recorded.blob.core.windows.net/" }), false));
}
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pii.Infrastructure;

public sealed class WorkspaceTelemetry : IDisposable
{
    private readonly TelemetryConfiguration? configuration;
    private readonly TelemetryClient? client;

    public WorkspaceTelemetry(IConfiguration settings, ITelemetryChannel? testChannel = null)
    {
        if (!settings.GetValue<bool>("Pii:Telemetry:Enabled")) return;
        var connection = settings["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Enabled telemetry requires APPLICATIONINSIGHTS_CONNECTION_STRING.");
        configuration = TelemetryConfiguration.CreateDefault();
        if (testChannel is not null)
        {
            configuration.TelemetryChannel.Dispose();
            configuration.TelemetryChannel = testChannel;
        }
        configuration.ConnectionString = connection;
        configuration.TelemetryInitializers.Clear();
        configuration.TelemetryProcessorChainBuilder.Use(next => new HealthOnlyProcessor(next)).Build();
        client = new TelemetryClient(configuration);
    }

    public bool Enabled => client is not null;

    public void Record(string host, bool succeeded, TimeSpan duration)
    {
        if (host is not ("Web" or "Worker")) throw new ArgumentException("Unknown telemetry host.", nameof(host));
        var health = new EventTelemetry("WorkspaceHealth");
        health.Properties["Host"] = host;
        health.Properties["Result"] = succeeded ? "Succeeded" : "Failed";
        health.Metrics["DurationMilliseconds"] = Math.Clamp(duration.TotalMilliseconds, 0, 3_600_000);
        client?.TrackEvent(health);
    }

    public async Task FlushAsync(CancellationToken cancellation)
    {
        if (client is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { await client.FlushAsync(timeout.Token); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    public void Dispose() => configuration?.Dispose();

    private sealed class HealthOnlyProcessor(ITelemetryProcessor next) : ITelemetryProcessor
    {
        public void Process(ITelemetry telemetry)
        {
            if (telemetry is not EventTelemetry { Name: "WorkspaceHealth" } health) return;
            foreach (var key in health.Properties.Keys.Where(key => key is not ("Host" or "Result")).ToList()) health.Properties.Remove(key);
            foreach (var key in health.Metrics.Keys.Where(key => key != "DurationMilliseconds").ToList()) health.Metrics.Remove(key);
            health.Context.User.Id = null;
            health.Context.Session.Id = null;
            health.Context.Operation.Id = null;
            health.Context.Operation.Name = null;
            health.Context.Operation.ParentId = null;
            health.Context.GlobalProperties.Clear();
            health.Context.Cloud.RoleInstance = null;
            health.Context.Location.Ip = "0.0.0.0";
            next.Process(health);
        }
    }
}

public static class WorkspaceTelemetryRegistration
{
    public static IServiceCollection AddWorkspaceTelemetry(this IServiceCollection services, IConfiguration settings) =>
        services.AddSingleton(_ => new WorkspaceTelemetry(settings));
}
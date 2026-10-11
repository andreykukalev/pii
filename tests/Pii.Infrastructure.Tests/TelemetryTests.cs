using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Configuration;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Infrastructure.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void EnabledTelemetryExportsOnlyBoundedHealthFields()
    {
        var channel = new RecordingChannel();
        using var telemetry = new WorkspaceTelemetry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pii:Telemetry:Enabled"] = "true",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=11111111-1111-1111-1111-111111111111"
        }).Build(), channel);
        telemetry.Record("Worker", false, TimeSpan.FromHours(2));
        var health = Assert.IsType<EventTelemetry>(Assert.Single(channel.Items));
        Assert.Equal("WorkspaceHealth", health.Name);
        Assert.Equal(2, health.Properties.Count);
        Assert.Equal("Worker", health.Properties["Host"]);
        Assert.Equal("Failed", health.Properties["Result"]);
        Assert.Equal(3_600_000, Assert.Single(health.Metrics).Value);
        Assert.Null(health.Context.Operation.Name);
        Assert.Null(health.Context.User.Id);
        Assert.Empty(health.Context.GlobalProperties);
    }

    [Fact]
    public async Task DisabledTelemetryNeedsNoConnectionOrNetwork()
    {
        using var telemetry = new WorkspaceTelemetry(new ConfigurationBuilder().Build());
        Assert.False(telemetry.Enabled);
        telemetry.Record("Web", true, TimeSpan.Zero);
        await telemetry.FlushAsync(CancellationToken.None);
    }

    [Fact]
    public void EnabledTelemetryRequiresConfiguration() => Assert.Throws<InvalidOperationException>(() =>
        new WorkspaceTelemetry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Pii:Telemetry:Enabled"] = "true" }).Build()));

    [Fact]
    public void HostNamesCannotCarryPrivateIdentifiers()
    {
        using var telemetry = new WorkspaceTelemetry(new ConfigurationBuilder().Build());
        Assert.Throws<ArgumentException>(() => telemetry.Record("private-run-id", false, TimeSpan.Zero));
    }

    private sealed class RecordingChannel : ITelemetryChannel
    {
        public List<ITelemetry> Items { get; } = [];
        public bool? DeveloperMode { get; set; }
        public string EndpointAddress { get; set; } = "";
        public void Send(ITelemetry item) => Items.Add(item);
        public void Flush() { }
        public void Dispose() { }
    }
}
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pii.Application;
using Pii.Domain;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Infrastructure.Tests;

public sealed class ResearchSourceTests
{
    private static readonly Instrument Instrument = new("SAMPLE:XNAS:USD", "SAMPLE", "XNAS", "USD", "Stock", "Synthetic Technology", "1234");
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static RuntimeConfiguration Runtime(ExecutionMode mode = ExecutionMode.Live, bool authorized = true) => new(mode, new(), [], "test", authorized, "test");
    private static RequestCreditAuthorization Credits(string endpoint, int weight = 1, int maximum = 1) => new()
    {
        Authorized = true, RateCardVersion = "synthetic-test-rates-not-vendor-pricing", MaximumCreditsPerRequest = maximum, DailyMaximumCredits = 100,
        EndpointWeights = new() { [endpoint] = weight }
    };
    private static ResearchSources RecordedSources(HttpClient client, ResearchSourceOptions options) =>
        new(client, options, Runtime(), false, new RecordedCreditStore());
    private sealed class RecordedCreditStore(SourceCreditAdmission admission = SourceCreditAdmission.Reserved) : ISourceCreditStore
    {
        public List<Guid> Reservations { get; } = [];
        public Task<SourceCreditAdmission> ReserveAsync(Guid reservationId, string endpoint, int credits, int dailyMaximumCredits,
            string rateCardVersion, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Reservations.Add(reservationId);
            return Task.FromResult(admission);
        }
    }
    private static ResearchSourceOptions Permitted(string endpoint = "eod", bool timezone = true, RequestCreditAuthorization? credits = null, bool retention = true,
        int companyFactsMaximumBytes = ResearchHttp.MaximumBytes, bool largerCompanyFacts = false) => new()
    {
        Authorized = true, SecRights = true, SecModelProcessingAllowed = false, SecUserAgent = "PiiTests contact@example.test",
        SecCompanyFactsMaximumBytes = companyFactsMaximumBytes, SecCompanyFactsLargerResponseAuthorized = largerCompanyFacts,
        EcbRights = true, EcbModelProcessingAllowed = true, TwelveDataRights = true, TwelveDataModelProcessingAllowed = true,
        TwelveDataApiKey = "synthetic-test-key", TwelveDataEntitlements = [endpoint],
        TwelveDataEvidenceRetentionAuthorized = retention, TwelveDataRequestCredits = credits ?? Credits(endpoint),
        ExchangeTimeZones = timezone ? new() { ["XNAS"] = "America/New_York" } : []
    };
    private const string Eod = """
        {"symbol":"SAMPLE","mic_code":"XNAS","currency":"USD","datetime":"2026-10-01","close":"120.123456789"}
        """;
    private const string Submissions = """
        {"cik":1234,"tickers":["SAMPLE"],"filings":{"recent":{
        "accessionNumber":["0000001234-26-000001"],"filingDate":["2026-10-01"],"acceptanceDateTime":["2026-10-01T18:00:00Z"],
        "form":["10-Q"],"reportDate":["2026-09-30"]}}}
        """;
    private const string CompanyFacts = """
        {"cik":1234,"facts":{"us-gaap":{"Revenues":{"units":{"USD":[
        {"val":123456.123456,"accn":"0000001234-26-000001","filed":"2026-10-01","form":"10-Q","end":"2026-09-30"}]}}}}}
        """;

    [Fact]
    public async Task DisabledRightsEntitlementsAndModesNeverInvokeHttp()
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        var disabled = new ResearchSources(client, new(), Runtime(), false);
        Assert.Equal("Unavailable", (await disabled.FetchAsync("eod", Instrument, ExecutionMode.Live, Cutoff, default)).Status);
        var entitled = new ResearchSources(client, Permitted("quote"), Runtime(), false);
        Assert.Equal("Unavailable", (await entitled.FetchAsync("eod", Instrument, ExecutionMode.Live, Cutoff, default)).Status);
        Assert.Equal("Unavailable", (await entitled.FetchAsync("eod", Instrument, ExecutionMode.FixtureReplay, Cutoff, default)).Status);
        var invalid = Instrument with { Id = "wrong" };
        Assert.Equal("Unavailable", (await entitled.FetchAsync("quote", invalid, ExecutionMode.Live, Cutoff, default)).Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task DocumentedEodShapeUsesConfiguredTimezoneAndDecimalPrecision()
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(Eod));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted()).FetchAsync("price", Instrument, ExecutionMode.Live, Cutoff, default);
        var quote = Assert.Single(result.Quotes);
        Assert.Equal(120.123456789m, quote.Price);
        Assert.Null(quote.EurPerUnit);
        Assert.Equal("Partial", result.Status);
        Assert.StartsWith("FxReferenceUnavailable:", result.Error);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero), quote.AsOf);
        Assert.Equal("apikey synthetic-test-key", transport.Authorizations[0]);
        Assert.Null(transport.Authorizations[1]);
        Assert.DoesNotContain("synthetic-test-key", transport.Uris[0].AbsoluteUri);
        Assert.Contains("mic_code=XNAS", transport.Uris[0].Query);
        Assert.DoesNotContain("synthetic-test-key", JsonDefaults.Serialize(result));
    }

    [Fact]
    public async Task MarketFxUsesQuoteAsOfAndRetainsOriginalAndCombinedNumericEvidence()
    {
        const string fx = """
            <Envelope xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube>
            <Cube time="2026-10-02"><Cube currency="USD" rate="2"/></Cube>
            <Cube time="2026-10-01"><Cube currency="USD" rate="1.25"/></Cube></Cube></Envelope>
            """;
        using var transport = new RecordedHttp((request, _) => RecordedHttp.Json(request.RequestUri!.Host == "www.ecb.europa.eu" ? fx : Eod));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted()).FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
        var quote = Assert.Single(result.Quotes);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(.8m, quote.EurPerUnit);
        Assert.Equal(120.123456789m, quote.Price);
        Assert.Equal(Instrument.Id, quote.InstrumentId);
        var original = Assert.Single(result.Evidence, item => item.Kind == "Price");
        var rate = Assert.Single(result.Evidence, item => item.Kind == "FxReference");
        var combined = Assert.Single(result.Evidence, item => item.Id == quote.EvidenceId);
        Assert.Equal(original.Numbers["Close"], combined.Numbers["Close"]);
        Assert.Equal(1.25m, combined.Numbers["UnitsPerEur"]);
        Assert.Equal(.8m, combined.Numbers["EurPerUnit"]);
        Assert.Equal(96.0987654312m, combined.Numbers["CloseEur"]);
        Assert.Contains(original.Id, combined.Provenance);
        Assert.Contains(rate.Id, combined.Provenance);
        Assert.Contains("not execution", combined.Provenance);
        Assert.True(rate.PublishedAt <= quote.AsOf);
        Assert.Equal(quote.AsOf, combined.PublishedAt);
        Assert.Equal(2, transport.Calls);
        Assert.Null(transport.Authorizations[1]);
    }

    [Fact]
    public async Task FutureOnlyFxRetainsNativePriceAndNullRateAsPartial()
    {
        const string fx = """
            <Envelope xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube>
            <Cube time="2026-10-02"><Cube currency="USD" rate="2"/></Cube></Cube></Envelope>
            """;
        using var transport = new RecordedHttp((request, _) => RecordedHttp.Json(request.RequestUri!.Host == "www.ecb.europa.eu" ? fx : Eod));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted()).FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Partial", result.Status);
        var quote = Assert.Single(result.Quotes);
        Assert.Null(quote.EurPerUnit);
        Assert.Equal(120.123456789m, quote.Price);
        Assert.Equal(quote.EvidenceId, Assert.Single(result.Evidence).Id);
    }

    [Fact]
    public async Task HistorySelectsFxPerBarWithOneEcbRequestAndConjunctiveProcessingRights()
    {
        const string history = """
            {"meta":{"symbol":"SAMPLE","mic_code":"XNAS","currency":"USD","exchange_timezone":"America/New_York"},
             "values":[{"datetime":"2026-10-01","close":"120"},{"datetime":"2026-09-30","close":"100"}]}
            """;
        const string fx = """
            <Envelope xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube>
            <Cube time="2026-10-01"><Cube currency="USD" rate="1.25"/></Cube>
            <Cube time="2026-09-30"><Cube currency="USD" rate="2"/></Cube></Cube></Envelope>
            """;
        using var transport = new RecordedHttp((request, _) => RecordedHttp.Json(request.RequestUri!.Host == "www.ecb.europa.eu" ? fx : history));
        using var client = new HttpClient(transport);
        var options = new ResearchSourceOptions
        {
            Authorized = true, EcbRights = true, EcbModelProcessingAllowed = false, TwelveDataRights = true,
            TwelveDataModelProcessingAllowed = true, TwelveDataApiKey = "synthetic-test-key", TwelveDataEntitlements = ["time_series"],
            TwelveDataEvidenceRetentionAuthorized = true, TwelveDataRequestCredits = Credits("time_series")
        };
        var result = await RecordedSources(client, options).FetchAsync("history", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(new decimal?[] { .8m, .5m }, result.Quotes.Select(item => item.EurPerUnit));
        Assert.All(result.Evidence.Where(item => item.Kind == "PriceWithFxReference"), item => Assert.False(item.ModelProcessingAllowed));
        Assert.Equal(2, transport.Calls);
    }

    [Theory]
    [InlineData(-1, "Partial")]
    [InlineData(0, "Succeeded")]
    public async Task QuoteFxAvailabilityBoundaryIsInclusiveAndNeverUsesLaterRunCutoff(int seconds, string status)
    {
        var asOf = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
        var body = $$"""{"symbol":"SAMPLE","mic_code":"XNAS","currency":"USD","timestamp":{{asOf.ToUnixTimeSeconds()}},"close":"120"}""";
        const string fx = """
            <Envelope xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube>
            <Cube time="2026-10-01"><Cube currency="USD" rate="1.25"/></Cube></Cube></Envelope>
            """;
        using var transport = new RecordedHttp((request, _) => RecordedHttp.Json(request.RequestUri!.Host == "www.ecb.europa.eu" ? fx : body));
        using var client = new HttpClient(transport);
        var sources = RecordedSources(client, Permitted("quote"));
        var result = await sources.FetchAsync("quote", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal(status, result.Status);
        Assert.Equal(asOf, Assert.Single(result.Quotes).AsOf);
        Assert.Equal(seconds == 0 ? .8m : (decimal?)null, result.Quotes[0].EurPerUnit);
        Assert.All(result.Evidence, item => Assert.True(item.PublishedAt <= asOf));
        var calls = transport.Calls;
        var futurePrice = await sources.FetchAsync("quote", Instrument, ExecutionMode.Live, asOf.AddTicks(-1), default);
        Assert.Equal("Unavailable", futurePrice.Status);
        Assert.Empty(futurePrice.Quotes);
        Assert.Equal(calls + 1, transport.Calls);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("currency")]
    [InlineData("rate")]
    [InlineData("http")]
    public async Task FxFailuresPreserveKnownNativePriceAndOriginalEvidence(string failure)
    {
        var fx = failure == "xml" ? "invalid-xml" : $$"""
            <Envelope xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube>
            <Cube time="2026-10-01"><Cube currency="{{(failure == "currency" ? "GBP" : "USD")}}" rate="{{(failure == "rate" ? "-1" : "1.25")}}"/></Cube></Cube></Envelope>
            """;
        using var transport = new RecordedHttp((request, _) => request.RequestUri!.Host == "www.ecb.europa.eu"
            ? RecordedHttp.Json(fx, failure == "http" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) : RecordedHttp.Json(Eod));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted()).FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Partial", result.Status);
        var quote = Assert.Single(result.Quotes);
        Assert.Null(quote.EurPerUnit);
        Assert.Equal(120.123456789m, quote.Price);
        Assert.Equal(quote.EvidenceId, Assert.Single(result.Evidence).Id);
        Assert.StartsWith("FxReferenceUnavailable:", result.Error);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task CallerCancellationDuringFxPropagatesAndIsNotPartialSuccess()
    {
        using var cancelled = new CancellationTokenSource();
        using var transport = new RecordedHttp((request, _) =>
        {
            if (request.RequestUri!.Host != "www.ecb.europa.eu") return RecordedHttp.Json(Eod);
            cancelled.Cancel();
            throw new OperationCanceledException(cancelled.Token);
        });
        using var client = new HttpClient(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordedSources(client, Permitted())
            .FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, cancelled.Token));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task EurQuoteUsesIdentityConversionWithoutEcbOrExecutedFxClaim()
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(Eod.Replace("USD", "EUR", StringComparison.Ordinal)));
        using var client = new HttpClient(transport);
        var instrument = Instrument with { Id = "SAMPLE:XNAS:EUR", Currency = "EUR" };
        var result = await RecordedSources(client, Permitted()).FetchAsync("market", instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(1m, Assert.Single(result.Quotes).EurPerUnit);
        Assert.Equal(result.Quotes[0].EvidenceId, Assert.Single(result.Evidence).Id);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("SAMPLE", "OTHER")]
    [InlineData("XNAS", "XNYS")]
    [InlineData("USD", "EUR")]
    public async Task EachProviderIdentityComponentMustMatchExactlyBeforeFx(string expected, string mismatch)
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(Eod.Replace(expected, mismatch, StringComparison.Ordinal)));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted()).FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("TwelveDataIdentityMismatch", result.Error);
        Assert.Empty(result.Quotes);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("missing-weight")]
    [InlineData("zero-weight")]
    [InlineData("negative-weight")]
    [InlineData("missing-rate-card")]
    [InlineData("missing-maximum")]
    [InlineData("missing-daily")]
    [InlineData("zero-daily")]
    [InlineData("negative-daily")]
    [InlineData("over-maximum")]
    [InlineData("retention")]
    public async Task UnknownUnauthorizedOrExcessRequestCreditsAndRetentionDenyBeforeIo(string scenario)
    {
        var authorization = scenario switch
        {
            "unauthorized" => new RequestCreditAuthorization(),
            "missing-weight" => Credits("quote"),
            "zero-weight" => Credits("eod", 0),
            "negative-weight" => Credits("eod", -1),
            "missing-rate-card" => new() { Authorized = true, DailyMaximumCredits = 100, MaximumCreditsPerRequest = 1, EndpointWeights = new() { ["eod"] = 1 } },
            "missing-maximum" => new() { Authorized = true, DailyMaximumCredits = 100, RateCardVersion = "recorded", EndpointWeights = new() { ["eod"] = 1 } },
            "missing-daily" => new() { Authorized = true, RateCardVersion = "recorded", MaximumCreditsPerRequest = 1, EndpointWeights = new() { ["eod"] = 1 } },
            "zero-daily" => new() { Authorized = true, RateCardVersion = "recorded", MaximumCreditsPerRequest = 1, DailyMaximumCredits = 0, EndpointWeights = new() { ["eod"] = 1 } },
            "negative-daily" => new() { Authorized = true, RateCardVersion = "recorded", MaximumCreditsPerRequest = 1, DailyMaximumCredits = -1, EndpointWeights = new() { ["eod"] = 1 } },
            "over-maximum" => Credits("eod", 5, 4),
            _ => Credits("eod")
        };
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        var store = new RecordedCreditStore();
        var result = await new ResearchSources(client, Permitted(credits: authorization, retention: scenario != "retention"), Runtime(), false, store)
            .FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Unavailable", result.Status);
        Assert.StartsWith("TwelveData", result.Error);
        Assert.Empty(result.Quotes);
        Assert.Equal(0, transport.Calls);
        Assert.Empty(store.Reservations);
    }

    [Fact]
    public async Task EntitledWeightedRequestAtMaximumHasCreditReservationProvenance()
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(Eod));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted(credits: Credits("eod", 5, 5)))
            .FetchAsync("eod", Instrument, ExecutionMode.Live, Cutoff, default);
        var price = Assert.Single(result.Evidence);
        Assert.Contains("configuredCreditWeight=5", price.Provenance);
        Assert.Contains("authorizedMaximumCreditsPerRequest=5", price.Provenance);
        Assert.Contains("reservationId=", price.Provenance);
        Assert.Contains("ChargedUncertain retained; not provider-reported usage", price.Provenance);
    }

    [Fact]
    public async Task LiveVendorWithMissingStoreNeverInvokesHttpEvenInDevelopment()
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        foreach (var development in new[] { false, true })
        {
            var result = await new ResearchSources(client, Permitted(), Runtime(), development)
                .FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
            Assert.Equal("TwelveDataSharedCreditStoreMissing", result.Error);
        }
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(SourceCreditAdmission.Exhausted, "TwelveDataDailyCreditBudgetExhausted")]
    [InlineData(SourceCreditAdmission.AlreadyReserved, "TwelveDataCreditReservationAlreadyUsed")]
    public async Task DeniedCreditStoreNeverInvokesVendorOrFxHttp(SourceCreditAdmission admission, string diagnostic)
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        var store = new RecordedCreditStore(admission);
        var result = await new ResearchSources(client, Permitted(), Runtime(), false, store)
            .FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal(diagnostic, result.Error);
        Assert.Single(store.Reservations);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task EachActualVendorRequestReservesFreshIdBeforeHttpWithoutRetryingFailedTransport()
    {
        var store = new RecordedCreditStore();
        var transportCalls = 0;
        using var transport = new RecordedHttp((_, _) =>
        {
            Assert.Equal(store.Reservations.Count, transportCalls + 1);
            transportCalls++;
            return RecordedHttp.Json("denied", HttpStatusCode.TooManyRequests);
        });
        using var client = new HttpClient(transport);
        var sources = new ResearchSources(client, Permitted(), Runtime(), false, store);
        await sources.FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
        await sources.FetchAsync("market", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(2, store.Reservations.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, store.Reservations);
    }

    [Theory]
    [InlineData("news")]
    [InlineData("announcements")]
    public async Task NewsWithoutCikUsesEntitledPressReleasesAndPreservesCoverageLimitation(string kind)
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json("""
            {"press_releases":[{"id":"recorded","datetime":"2026-10-01T18:00:00Z","title":"Synthetic ETF disclosure"}]}
            """));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted("press_releases"))
            .FetchAsync(kind, Instrument with { Cik = null, Kind = "ETF" }, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Partial", result.Status);
        Assert.Equal("CorporateAnnouncement", Assert.Single(result.Evidence).Kind);
        Assert.Contains("SecCikUnavailable", result.Error);
        Assert.Contains("not comprehensive news", result.Error);
        Assert.Equal("/press_releases", Assert.Single(transport.Uris).AbsolutePath);
    }

    [Fact]
    public async Task NewsWithoutSecOrPressEntitlementIsUnavailableAndNeverUsesFixture()
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        var result = await new ResearchSources(client, Permitted(), Runtime(), true)
            .FetchAsync("news", Instrument with { Cik = null }, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Unavailable", result.Status);
        Assert.Contains("EndpointEntitlement", result.Error);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task SecIdentityMismatchDoesNotInvokePressFallback()
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json("""{"cik":9999,"tickers":["SAMPLE"]}"""));
        using var client = new HttpClient(transport);
        var result = await new ResearchSources(client, Permitted("press_releases"), Runtime(), false)
            .FetchAsync("news", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("SecIdentityMismatch", result.Error);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyFactsAboveOneMiBRequiresSeparateApprovedBound(bool approved)
    {
        var body = CompanyFacts + new string(' ', ResearchHttp.MaximumBytes);
        using var transport = new RecordedHttp((request, _) => RecordedHttp.Json(request.RequestUri!.AbsolutePath.StartsWith("/submissions/", StringComparison.Ordinal) ? Submissions : body));
        using var client = new HttpClient(transport);
        var options = Permitted(companyFactsMaximumBytes: approved ? 2 * ResearchHttp.MaximumBytes : ResearchHttp.MaximumBytes, largerCompanyFacts: approved);
        var result = await new ResearchSources(client, options, Runtime(), false).FetchAsync("financial", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal(approved ? "Partial" : "Unavailable", result.Status);
        if (approved) Assert.Equal(123456.123456m, Assert.Single(result.Evidence).Numbers["Revenues"]);
        else Assert.Empty(result.Evidence);
        Assert.Equal(2, transport.Calls);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(8388609, true)]
    [InlineData(2097152, false)]
    public async Task UnapprovedOrUnboundedCompanyFactsLimitsDenyBeforeAnyHttp(int maximum, bool approved)
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        var result = await new ResearchSources(client, Permitted(companyFactsMaximumBytes: maximum, largerCompanyFacts: approved), Runtime(), false)
            .FetchAsync("financial", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("SecCompanyFactsResponseLimitNotAuthorizedOrOutOfBounds", result.Error);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist-90d.xml")]
    [InlineData("https://data.sec.gov/submissions/CIK0000001234.json")]
    [InlineData("https://api.twelvedata.com/eod?symbol=SAMPLE")]
    [InlineData("https://data.sec.gov/api/xbrl/companyfacts/CIKnot-a-cik.json")]
    public async Task LargerCompanyFactsLimitCannotWidenOtherEndpointResponses(string endpoint)
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<SourceUnavailableException>(() => ResearchHttp.GetAsync(client, new Uri(endpoint), null, null, default, 2 * ResearchHttp.MaximumBytes));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompanyFactsConfiguredBoundRejectsOversizeWithAndWithoutContentLength(bool streamed)
    {
        using var transport = new RecordedHttp((request, _) => request.RequestUri!.AbsolutePath.StartsWith("/submissions/", StringComparison.Ordinal)
            ? RecordedHttp.Json(Submissions)
            : streamed ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableReadStream(new byte[2 * ResearchHttp.MaximumBytes + 1])) }
            : RecordedHttp.Json(new string(' ', 2 * ResearchHttp.MaximumBytes + 1)));
        using var client = new HttpClient(transport);
        var result = await new ResearchSources(client, Permitted(companyFactsMaximumBytes: 2 * ResearchHttp.MaximumBytes, largerCompanyFacts: true), Runtime(), false)
            .FetchAsync("financial", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Unavailable", result.Status);
        Assert.Empty(result.Evidence);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task CompanyFactsExaminesOnlyLastThousandEntriesPerTag()
    {
        var eligible = new { val = 123456.123456m, accn = "0000001234-26-000001", filed = "2026-10-01", form = "10-Q", end = "2026-09-30" };
        var future = eligible with { filed = "2026-10-09" };
        var values = new[] { eligible }.Concat(Enumerable.Repeat(future, 1000)).ToArray();
        var body = JsonDefaults.Serialize(new { cik = 1234, facts = new Dictionary<string, object> { ["us-gaap"] = new { Revenues = new { units = new { USD = values } } } } });
        using var transport = new RecordedHttp((request, _) => RecordedHttp.Json(request.RequestUri!.AbsolutePath.StartsWith("/submissions/", StringComparison.Ordinal) ? Submissions : body));
        using var client = new HttpClient(transport);
        var result = await new ResearchSources(client, Permitted(), Runtime(), false).FetchAsync("financial", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Unavailable", result.Status);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void SourceConfigurationBindsWorkerOnlyAdmissionAndSizeApprovalAndDefaultsFailClosed()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pii:Sources:Sec:RightsAuthorized"] = "true", ["Pii:Sources:Sec:CompanyFactsMaximumBytes"] = "2097152",
            ["Pii:Sources:Sec:CompanyFactsLargerResponseAuthorized"] = "true",
            ["Pii:Sources:TwelveData:RightsAuthorized"] = "true", ["Pii:Sources:TwelveData:EvidenceRetentionAuthorized"] = "true",
            ["Pii:Sources:TwelveData:RequestCreditAuthorization:Authorized"] = "true",
            ["Pii:Sources:TwelveData:RequestCreditAuthorization:RateCardVersion"] = "synthetic-rate-card",
            ["Pii:Sources:TwelveData:RequestCreditAuthorization:MaximumCreditsPerRequest"] = "5",
            ["Pii:Sources:TwelveData:RequestCreditAuthorization:DailyMaximumCredits"] = "20",
            ["Pii:Sources:TwelveData:RequestCreditAuthorization:EndpointWeights:eod"] = "5",
            ["Pii:Sources:TwelveData:Entitlements:0"] = "eod"
        }).Build();
        var worker = InfrastructureRegistration.ReadResearchSourceOptions(configuration, true, true);
        Assert.True(worker.Authorized);
        Assert.True(worker.TwelveDataEvidenceRetentionAuthorized);
        Assert.True(worker.SecCompanyFactsLargerResponseAuthorized);
        Assert.Equal(2097152, worker.SecCompanyFactsMaximumBytes);
        Assert.Equal(5, worker.TwelveDataRequestCredits.MaximumCreditsPerRequest);
        Assert.Equal(20, worker.TwelveDataRequestCredits.DailyMaximumCredits);
        Assert.Equal(5, worker.TwelveDataRequestCredits.EndpointWeights["eod"]);
        var web = InfrastructureRegistration.ReadResearchSourceOptions(configuration, false, true);
        Assert.False(web.Authorized);
        Assert.False(web.SecRights);
        Assert.False(web.TwelveDataRights);
        Assert.False(web.TwelveDataEvidenceRetentionAuthorized);
        Assert.False(web.TwelveDataRequestCredits.Authorized);
        Assert.Null(web.TwelveDataRequestCredits.DailyMaximumCredits);
        Assert.False(web.SecCompanyFactsLargerResponseAuthorized);
        Assert.Empty(web.TwelveDataEntitlements);
        Assert.Null(web.TwelveDataApiKey);
        var defaults = InfrastructureRegistration.ReadResearchSourceOptions(new ConfigurationBuilder().Build(), true, true);
        Assert.False(defaults.TwelveDataRequestCredits.Authorized);
        Assert.Null(defaults.TwelveDataRequestCredits.DailyMaximumCredits);
        Assert.False(defaults.TwelveDataEvidenceRetentionAuthorized);
        Assert.Equal(ResearchHttp.MaximumBytes, defaults.SecCompanyFactsMaximumBytes);
        Assert.False(defaults.SecCompanyFactsLargerResponseAuthorized);
    }

    [Fact]
    public void FixtureModelDiResolvesDirectImplementationInDevelopmentWorker()
    {
        using var provider = new ServiceCollection().AddPiiInfrastructure(new ConfigurationBuilder().Build(), true, true).BuildServiceProvider();
        Assert.IsType<FixtureModelGateway>(provider.GetRequiredService<IModelGateway>());
    }

    private sealed class UnseekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Theory]
    [InlineData("wrong-identity")]
    [InlineData("future")]
    [InlineData("missing-timezone")]
    public async Task UnverifiedFutureOrUnzonedDailyBarsAreUnavailable(string scenario)
    {
        var body = scenario == "wrong-identity" ? Eod.Replace("XNAS", "XNYS", StringComparison.Ordinal)
            : scenario == "future" ? Eod.Replace("2026-10-01", "2026-10-05", StringComparison.Ordinal) : Eod;
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(body));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted(timezone: scenario != "missing-timezone"))
            .FetchAsync("eod", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Unavailable", result.Status);
        Assert.Empty(result.Quotes);
    }

    [Fact]
    public async Task RawHistoryRetainsDatesAndRequestsUnadjustedDailyValues()
    {
        const string body = """
            {"meta":{"symbol":"SAMPLE","mic_code":"XNAS","currency":"USD","exchange_timezone":"America/New_York"},
             "values":[{"datetime":"2026-10-01","close":"120.50"},{"datetime":"2026-09-30","close":"119.75"}]}
            """;
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(body));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted("time_series")).FetchAsync("history", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal(2, result.Quotes.Count);
        Assert.Contains("adjust=none", transport.Uris[0].Query);
        Assert.Contains("interval=1day", transport.Uris[0].Query);
    }

    [Fact]
    public async Task QuoteUsesProviderTimestampAndPressReleasesExcludeBodyHtml()
    {
        var timestamp = new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        using var transport = new RecordedHttp((request, _) => RecordedHttp.Json(request.RequestUri!.AbsolutePath == "/quote"
            ? $$"""{"symbol":"SAMPLE","mic_code":"XNAS","currency":"USD","timestamp":{{timestamp}},"close":"121.5"}"""
            : """{"press_releases":[{"id":"recorded-1","datetime":"2026-10-01T20:00:00Z","title":"Synthetic disclosure","body":"<script>secret-body</script>"},{"id":"future","datetime":"2026-10-10T20:00:00Z","title":"Future"}]}"""));
        using var client = new HttpClient(transport);
        var quote = await RecordedSources(client, Permitted("quote")).FetchAsync("quote", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal(timestamp, Assert.Single(quote.Quotes).AsOf.ToUnixTimeSeconds());
        var press = await RecordedSources(client, Permitted("press_releases")).FetchAsync("press_releases", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Partial", press.Status);
        Assert.Equal("Synthetic disclosure", Assert.Single(press.Evidence).Text);
        Assert.DoesNotContain("secret-body", JsonDefaults.Serialize(press));
    }

    [Fact]
    public async Task SecFinancialPeriodIsNotPublicationAndDecimalsAreNotRounded()
    {
        const string submissions = """
            {"cik":1234,"tickers":["SAMPLE"],"filings":{"recent":{
            "accessionNumber":["0000001234-26-000001","0000001234-26-000002"],
            "filingDate":["2026-10-01","2026-10-09"],"acceptanceDateTime":["2026-10-01T18:00:00Z","2026-10-09T18:00:00Z"],
            "form":["10-Q","10-Q"],"reportDate":["2026-09-30","2026-09-30"]}}}
            """;
        const string facts = """
            {"cik":1234,"facts":{"us-gaap":{"Revenues":{"units":{"USD":[
            {"val":123456.123456,"accn":"0000001234-26-000001","filed":"2026-10-01","form":"10-Q","start":"2026-07-01","end":"2026-09-30"},
            {"val":999999,"accn":"0000001234-26-000002","filed":"2026-10-09","form":"10-Q","end":"2026-09-30"}]}}}}}
            """;
        using var transport = new RecordedHttp((request, _) => RecordedHttp.Json(request.RequestUri!.AbsolutePath.StartsWith("/submissions/", StringComparison.Ordinal) ? submissions : facts));
        using var client = new HttpClient(transport);
        var sources = new ResearchSources(client, Permitted(), Runtime(), false);
        var result = await sources.FetchAsync("facts", Instrument, ExecutionMode.Live, Cutoff, default);
        var fact = Assert.Single(result.Evidence);
        Assert.Equal(123456.123456m, fact.Numbers["Revenues"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero), fact.PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), fact.PeriodEnd);
        Assert.False(fact.ModelProcessingAllowed);
        Assert.Contains("accession=0000001234-26-000001", fact.Provenance);
        var unavailable = await sources.FetchAsync("facts", Instrument, ExecutionMode.Live, fact.PeriodEnd!.Value, default);
        Assert.Empty(unavailable.Evidence);
    }

    [Fact]
    public async Task EcbParsesStructuralXmlUsesReciprocalAndConservativeAvailability()
    {
        const string body = """
            <Envelope xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube>
            <Cube time="2026-10-02"><Cube currency="USD" rate="1.25"/></Cube>
            <Cube time="2026-10-01"><Cube currency="USD" rate="1.20"/></Cube></Cube></Envelope>
            """;
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(body));
        using var client = new HttpClient(transport);
        var sources = new ResearchSources(client, Permitted(), Runtime(), false);
        var result = await sources.FetchAsync("fx", Instrument, ExecutionMode.Live, Cutoff, default);
        var rate = Assert.Single(result.Evidence);
        Assert.Equal(.8m, rate.Numbers["EurPerUnit"]);
        Assert.Equal(1.25m, rate.Numbers["UnitsPerEur"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), rate.PublishedAt);
        Assert.Empty((await sources.FetchAsync("fx", Instrument, ExecutionMode.Live, Cutoff.AddDays(-10), default)).Evidence);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("redirect")]
    [InlineData("rate-limit")]
    [InlineData("oversize")]
    [InlineData("doctype")]
    public async Task FailedTransportAndUnsafePayloadsHaveExplicitSanitizedResults(string scenario)
    {
        using var transport = new RecordedHttp((_, _) => RecordedHttp.Json(scenario switch
        {
            "oversize" => new string('x', ResearchHttp.MaximumBytes + 1),
            "doctype" => "<!DOCTYPE a [<!ENTITY entity SYSTEM 'file:///C:/private'>]><a>&entity;</a>",
            _ => "not-json synthetic-test-key"
        }, scenario == "redirect" ? HttpStatusCode.Found : scenario == "rate-limit" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK));
        using var client = new HttpClient(transport);
        var result = await RecordedSources(client, Permitted()).FetchAsync(scenario == "doctype" ? "fx" : "eod", Instrument, ExecutionMode.Live, Cutoff, default);
        Assert.Equal("Unavailable", result.Status);
        Assert.DoesNotContain("synthetic-test-key", JsonDefaults.Serialize(result));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task StaticFixturesAreDatedIndependentAndDevelopmentOnly()
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        var sources = new ResearchSources(client, new(), Runtime(ExecutionMode.FixtureReplay, false), true);
        var item = SyntheticResearchFixture.Catalog[1];
        var result = await sources.FetchAsync("facts", item, ExecutionMode.FixtureReplay, Cutoff, default);
        Assert.Equal(80_000_000m, Assert.Single(result.Evidence).Numbers["Revenue"]);
        Assert.Equal(SyntheticResearchFixture.PublishedAt, result.Evidence[0].PublishedAt);
        Assert.Empty((await sources.FetchAsync("facts", item, ExecutionMode.FixtureReplay, SyntheticResearchFixture.PublishedAt.AddTicks(-1), default)).Evidence);
        Assert.Empty(SyntheticResearchFixture.Fetch("facts", item with { Sector = "changed" }, Cutoff).Evidence);
        Assert.Empty((await new ResearchSources(client, new(), Runtime(ExecutionMode.FixtureReplay), false).FetchAsync("facts", item, ExecutionMode.FixtureReplay, Cutoff, default)).Evidence);
        Assert.Equal(0, transport.Calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sources.FetchAsync("facts", item, ExecutionMode.FixtureReplay, Cutoff, cancelled.Token));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("symbol")]
    [InlineData("exchange")]
    [InlineData("currency")]
    [InlineData("kind")]
    [InlineData("sector")]
    public void FixtureIdentityGuardRejectsAlteredIdentityComponents(string component)
    {
        var original = SyntheticResearchFixture.Catalog[0];
        var altered = component switch
        {
            "id" => original with { Id = "WRONG:XNAS:USD" },
            "symbol" => original with { Symbol = "OTHER" },
            "exchange" => original with { Exchange = "XNYS" },
            "currency" => original with { Currency = "EUR" },
            "kind" => original with { Kind = "ETF" },
            _ => original with { Sector = "changed" }
        };
        Assert.Equal("NoSyntheticFixtureForInstrument", SyntheticResearchFixture.Fetch("market", altered, Cutoff).Error);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    public void SourceTransportRejectsPrivateNetworkAddresses(string address) => Assert.False(ResearchHttp.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task SourceTransportRejectsArbitraryEndpointsBeforeHttp()
    {
        using var transport = new RecordedHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<SourceUnavailableException>(() => ResearchHttp.GetAsync(client, new Uri("http://127.0.0.1/"), null, null, default));
        await Assert.ThrowsAsync<SourceUnavailableException>(() => ResearchHttp.GetAsync(client, new Uri("https://attacker.test/"), null, null, default));
        Assert.Equal(0, transport.Calls);
    }
}
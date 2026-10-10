using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Pii.Application;
using Pii.Domain;

namespace Pii.Infrastructure;

public sealed class RequestCreditAuthorization
{
    public bool Authorized { get; init; }
    public string? RateCardVersion { get; init; }
    public int? MaximumCreditsPerRequest { get; init; }
    public int? DailyMaximumCredits { get; init; }
    public Dictionary<string, int> EndpointWeights { get; init; } = new(StringComparer.Ordinal);

    internal string? Diagnostic(string endpoint)
    {
        if (!Authorized) return "TwelveDataRequestCreditsNotAuthorized";
        if (DailyMaximumCredits is not > 0) return "TwelveDataDailyMaximumCreditsMissingOrInvalid";
        if (string.IsNullOrWhiteSpace(RateCardVersion) || RateCardVersion.Length > 128 || RateCardVersion.Any(char.IsControl)
            || MaximumCreditsPerRequest is not > 0 || !EndpointWeights.TryGetValue(endpoint, out var weight) || weight <= 0)
            return "TwelveDataEndpointCreditWeightOrRateCardUnknown";
        return weight > MaximumCreditsPerRequest.Value ? "TwelveDataEndpointExceedsAuthorizedRequestCredits" : null;
    }
}

public sealed class ResearchSourceOptions
{
    public bool Authorized { get; init; }
    public bool SecRights { get; init; }
    public bool SecModelProcessingAllowed { get; init; }
    public string? SecUserAgent { get; init; }
    public int SecCompanyFactsMaximumBytes { get; init; } = ResearchHttp.MaximumBytes;
    public bool SecCompanyFactsLargerResponseAuthorized { get; init; }
    public bool EcbRights { get; init; }
    public bool EcbModelProcessingAllowed { get; init; }
    public bool TwelveDataRights { get; init; }
    public bool TwelveDataEvidenceRetentionAuthorized { get; init; }
    public bool TwelveDataModelProcessingAllowed { get; init; }
    public string? TwelveDataApiKey { get; init; }
    public HashSet<string> TwelveDataEntitlements { get; init; } = [];
    public RequestCreditAuthorization TwelveDataRequestCredits { get; init; } = new();
    public Dictionary<string, string> ExchangeTimeZones { get; init; } = [];
}

public sealed class ResearchSources(HttpClient client, ResearchSourceOptions options, RuntimeConfiguration runtime, bool development,
    ISourceCreditStore? creditStore = null) : IResearchSources
{
    public async Task<SourceResult> FetchAsync(string kind, Instrument instrument, ExecutionMode mode, DateTimeOffset cutoff, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (mode != runtime.Mode) return Unavailable("ExecutionModeMismatch");
        if (mode is ExecutionMode.FixtureReplay or ExecutionMode.ModelOnFixtures)
            return development ? SyntheticResearchFixture.Fetch(kind, instrument, cutoff) : Unavailable("FixturesForbiddenInProduction");
        if (!options.Authorized || !runtime.LiveAuthorized) return Unavailable("LiveSourcesDisabledOrWebHost");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            ResearchHttp.ValidateInstrument(instrument);
            var endpoint = NormalizeKind(kind);
            var result = endpoint switch
            {
                "submissions" when kind.Equals("news", StringComparison.OrdinalIgnoreCase) || kind.Equals("announcements", StringComparison.OrdinalIgnoreCase)
                    => await NewsAsync(instrument, cutoff, deadline.Token),
                "companyfacts" or "submissions" => await SecAsync(endpoint, instrument, cutoff, deadline.Token),
                "fx" => await EcbAsync(instrument, cutoff, deadline.Token),
                "eod" or "quote" or "time_series" or "press_releases" => await TwelveDataAsync(endpoint, instrument, cutoff, deadline.Token),
                _ => Unavailable("UnsupportedSourceFieldOrKind")
            };
            if (result.Quotes.Count == 0 || instrument.Currency == "EUR") return result;
            try { return await AttachFxAsync(result, instrument, deadline.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { return result with { Status = "Partial", Error = "FxReferenceUnavailable:SourceTimeout20Seconds" }; }
            catch (SourceUnavailableException error)
            { return result with { Status = "Partial", Error = "FxReferenceUnavailable:" + error.Message }; }
            catch (Exception error) when (error is JsonException or XmlException or FormatException or InvalidDataException or InvalidOperationException
                or KeyNotFoundException or ArgumentException or HttpRequestException or OverflowException or IndexOutOfRangeException)
            { return result with { Status = "Partial", Error = "FxReferenceUnavailable:MalformedMissingIdentityOrBoundedSourceResponse" }; }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return Unavailable("SourceTimeout20Seconds"); }
        catch (SourceUnavailableException error) { return Unavailable(error.Message); }
        catch (Exception error) when (error is JsonException or XmlException or FormatException or InvalidDataException or InvalidOperationException
            or KeyNotFoundException or ArgumentException or HttpRequestException or OverflowException or IndexOutOfRangeException)
        { return Unavailable("MalformedMissingIdentityOrBoundedSourceResponse"); }
    }

    internal static string NormalizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        "financial" or "financials" or "facts" or "sec-companyfacts" => "companyfacts",
        "news" or "announcements" or "sec-submissions" => "submissions",
        "prices" or "price" or "market" => "eod",
        "history" or "pricehistory" => "time_series",
        "rates" or "ecb" => "fx",
        "press-releases" => "press_releases",
        var value => value
    };
    public static SourceResult Unavailable(string reason) => new([], [], "Unavailable", reason);
    private static SourceResult Result(List<EvidenceRecord> evidence, List<Quote>? quotes = null, string? limitation = null) =>
        evidence.Count == 0 ? Unavailable(limitation ?? "NoDatedPermittedDataAtCutoff") : new(evidence, quotes ?? [], limitation is null ? "Succeeded" : "Partial", limitation);

    private async Task<SourceResult> NewsAsync(Instrument instrument, DateTimeOffset cutoff, CancellationToken cancellation)
    {
        var filings = await SecAsync("submissions", instrument, cutoff, cancellation);
        if (filings.Status != "Unavailable" || filings.Error is not ("SecCikUnavailable" or "SecRequiresSeparateRightsAndIdentifiedUserAgentContact")) return filings;
        var releases = await TwelveDataAsync("press_releases", instrument, cutoff, cancellation);
        return releases.Status == "Unavailable" ? Unavailable($"NewsUnavailable:SEC={filings.Error};PressReleases={releases.Error}")
            : releases with { Status = "Partial", Error = $"{releases.Error}; SEC unavailable ({filings.Error}); entitled press releases only, not comprehensive news." };
    }

    private async Task<SourceResult> SecAsync(string kind, Instrument instrument, DateTimeOffset cutoff, CancellationToken cancellation)
    {
        if (kind == "companyfacts" && (options.SecCompanyFactsMaximumBytes <= 0 || options.SecCompanyFactsMaximumBytes > ResearchHttp.MaximumCompanyFactsBytes
            || options.SecCompanyFactsMaximumBytes > ResearchHttp.MaximumBytes && !options.SecCompanyFactsLargerResponseAuthorized))
            return Unavailable("SecCompanyFactsResponseLimitNotAuthorizedOrOutOfBounds");
        if (!options.SecRights || string.IsNullOrWhiteSpace(options.SecUserAgent)
            || !options.SecUserAgent.Contains('@') || options.SecUserAgent.Any(char.IsControl)) return Unavailable("SecRequiresSeparateRightsAndIdentifiedUserAgentContact");
        if (instrument.Cik is null || instrument.Cik.Length is < 1 or > 10 || !instrument.Cik.All(char.IsAsciiDigit)) return Unavailable("SecCikUnavailable");
        var cik = instrument.Cik.PadLeft(10, '0');
        var submissionsUri = new Uri($"https://data.sec.gov/submissions/CIK{cik}.json");
        using var submissions = JsonDocument.Parse(await ResearchHttp.GetAsync(client, submissionsUri, options.SecUserAgent, null, cancellation));
        var root = submissions.RootElement;
        if (Cik(root.GetProperty("cik")) != cik || !root.GetProperty("tickers").EnumerateArray().Any(item => item.GetString() == instrument.Symbol))
            return Unavailable("SecIdentityMismatch");
        var recent = root.GetProperty("filings").GetProperty("recent");
        var publication = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var evidence = new List<EvidenceRecord>();
        var accessionNumbers = recent.GetProperty("accessionNumber");
        for (var index = 0; index < Math.Min(accessionNumbers.GetArrayLength(), 1000); index++)
        {
            var accession = accessionNumbers[index].GetString() ?? "";
            if (accession.Length != 20 || !accession.All(character => char.IsAsciiDigit(character) || character == '-')) continue;
            var date = recent.GetProperty("filingDate")[index].GetString() ?? "";
            var published = recent.TryGetProperty("acceptanceDateTime", out var acceptances) && index < acceptances.GetArrayLength()
                && TryTimestamp(acceptances[index].GetString(), out var accepted) ? accepted : DateEndInNewYork(date);
            if (published > cutoff) continue;
            publication[accession] = published;
            if (kind != "submissions" || evidence.Count >= 20) continue;
            var form = recent.GetProperty("form")[index].GetString() ?? "Unknown";
            var reportDate = recent.GetProperty("reportDate")[index].GetString();
            DateTimeOffset? periodEnd = DateOnly.TryParseExact(reportDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var period)
                ? new DateTimeOffset(period.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
            var url = $"https://www.sec.gov/Archives/edgar/data/{long.Parse(cik, CultureInfo.InvariantCulture)}/{accession.Replace("-", "", StringComparison.Ordinal)}/{accession}-index.html";
            evidence.Add(Evidence(instrument, "SEC", url, "Filing", published, $"{form} filing; not comprehensive news or a forecast.", [], options.SecModelProcessingAllowed,
                $"submissions/filings/recent/{index}; accession={accession}; filingDate={date}; publication=acceptanceDateTime or conservative NY date-end", periodEnd));
        }
        if (kind == "submissions") return Result(evidence, limitation: "Recent filing metadata only; no comprehensive news or older-file pagination.");
        var uri = new Uri($"https://data.sec.gov/api/xbrl/companyfacts/CIK{cik}.json");
        using var document = JsonDocument.Parse(await ResearchHttp.GetAsync(client, uri, options.SecUserAgent, null, cancellation, options.SecCompanyFactsMaximumBytes));
        if (Cik(document.RootElement.GetProperty("cik")) != cik) return Unavailable("SecIdentityMismatch");
        if (!document.RootElement.GetProperty("facts").TryGetProperty("us-gaap", out var taxonomy)) return Unavailable("SupportedUsGaapTaxonomyUnavailable");
        string[] tags = ["Revenues", "RevenueFromContractWithCustomerExcludingAssessedTax", "NetIncomeLoss", "Assets", "Liabilities", "CashAndCashEquivalentsAtCarryingValue"];
        foreach (var tag in tags)
        {
            if (!taxonomy.TryGetProperty(tag, out var fact) || !fact.GetProperty("units").TryGetProperty(instrument.Currency, out var values)) continue;
            for (var valueIndex = values.GetArrayLength() - 1; valueIndex >= Math.Max(0, values.GetArrayLength() - 1000); valueIndex--)
            {
                if (evidence.Count >= 40) break;
                var value = values[valueIndex];
                if (!value.TryGetProperty("filed", out var filed) || !value.TryGetProperty("accn", out var accession)) continue;
                var accessionId = accession.GetString() ?? "";
                if (!publication.TryGetValue(accessionId, out var published)) continue;
                if (DateEndInNewYork(filed.GetString() ?? "") > cutoff) continue;
                var periodEnd = DateUtc(ResearchHttp.Text(value, "end"));
                if (periodEnd > cutoff) continue;
                var number = ResearchHttp.Number(value.GetProperty("val"));
                var start = value.TryGetProperty("start", out var startElement) ? startElement.GetString() : null;
                var form = ResearchHttp.Text(value, "form");
                evidence.Add(Evidence(instrument, "SEC", uri.AbsoluteUri, "FinancialFact", published, $"Reported {tag}; unit {instrument.Currency}; not a derived ratio.",
                    new() { [tag] = number }, options.SecModelProcessingAllowed,
                    $"facts/us-gaap/{tag}/units/{instrument.Currency}; accession={accessionId}; form={form}; start={start}; end={periodEnd:yyyy-MM-dd}; filed={filed.GetString()}; publication joined to submissions", periodEnd));
            }
        }
        return Result(evidence, limitation: "Bounded reported US-GAAP fields; periods/restatements kept distinct; last 1000 entries examined per tag. Missing tags, non-US/ETF fields and filings outside recent submissions unavailable.");
    }

    private async Task<SourceResult> EcbAsync(Instrument instrument, DateTimeOffset cutoff, CancellationToken cancellation)
    {
        if (!options.EcbRights) return Unavailable("EcbRightsNotAuthorized");
        return EcbAt(instrument, cutoff, await ReadEcbAsync(cancellation));
    }

    private async Task<XDocument> ReadEcbAsync(CancellationToken cancellation)
    {
        var uri = new Uri("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist-90d.xml");
        var bytes = await ResearchHttp.GetAsync(client, uri, null, null, cancellation);
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ResearchHttp.MaximumBytes });
        return XDocument.Load(reader);
    }

    private SourceResult EcbAt(Instrument instrument, DateTimeOffset cutoff, XDocument document)
    {
        const string url = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist-90d.xml";
        XNamespace schema = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";
        foreach (var cube in document.Descendants(schema + "Cube").Where(item => item.Attribute("time") is not null).OrderByDescending(item => (string?)item.Attribute("time")))
        {
            var date = DateUtc((string)cube.Attribute("time")!);
            var availableBy = date.AddDays(1);
            if (availableBy > cutoff) continue;
            var rateElement = cube.Elements(schema + "Cube").SingleOrDefault(item => (string?)item.Attribute("currency") == instrument.Currency);
            var perEur = instrument.Currency == "EUR" ? 1m : rateElement is null ? (decimal?)null
                : decimal.Parse((string)rateElement.Attribute("rate")!, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (perEur is not > 0) return Unavailable("EcbCurrencyUnavailable");
            return Result([Evidence(instrument, "ECB", url, "FxReference", availableBy,
                "EUR reference rate, not executed FX; date-only feed uses conservative next-UTC-day availability bound.",
                new() { ["UnitsPerEur"] = perEur.Value, ["EurPerUnit"] = 1m / perEur.Value }, options.EcbModelProcessingAllowed,
                $"Cube[@time='{date:yyyy-MM-dd}']/Cube[@currency='{instrument.Currency}']; last dated rate before cutoff; actual publication time not supplied", date)]);
        }
        return Unavailable("EcbDateOutside90DayCoverageOrNotYetAvailable");
    }

    private async Task<SourceResult> AttachFxAsync(SourceResult prices, Instrument instrument, CancellationToken cancellation)
    {
        if (!options.EcbRights) return prices with { Status = "Partial", Error = "FxReferenceUnavailable:EcbRightsNotAuthorized" };
        var document = await ReadEcbAsync(cancellation);
        var evidence = new List<EvidenceRecord>(prices.Evidence);
        var quotes = new List<Quote>();
        string? limitation = prices.Error;
        foreach (var quote in prices.Quotes)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = EcbAt(instrument, quote.AsOf, document);
            if (result.Evidence.Count == 0)
            {
                quotes.Add(quote);
                limitation = "FxReferenceUnavailable:" + result.Error;
                continue;
            }
            var rate = result.Evidence[0];
            var original = prices.Evidence.Single(item => item.Id == quote.EvidenceId);
            var combined = Evidence(instrument, "TwelveData+ECB", original.Url, "PriceWithFxReference", quote.AsOf,
                "Dated native close with EUR valuation reference; decimal reciprocal of ECB units per EUR, not executed trade FX.",
                new() { ["Close"] = quote.Price, ["UnitsPerEur"] = rate.Numbers["UnitsPerEur"], ["EurPerUnit"] = rate.Numbers["EurPerUnit"],
                    ["CloseEur"] = quote.Price * rate.Numbers["EurPerUnit"] },
                original.ModelProcessingAllowed && rate.ModelProcessingAllowed,
                $"calculation=ECB reciprocal and native close * EurPerUnit; instrument={instrument.Id}; currency={quote.Currency}; priceEvidenceId={original.Id}; fxEvidenceId={rate.Id}; quoteAsOf={quote.AsOf:O}; fxAvailableBy={rate.PublishedAt:O}; fxDate={rate.PeriodEnd:yyyy-MM-dd}; reference only, not execution",
                rate.PeriodEnd);
            if (!evidence.Any(item => item.Id == rate.Id)) evidence.Add(rate);
            evidence.Add(combined);
            quotes.Add(quote with { EurPerUnit = rate.Numbers["EurPerUnit"], EvidenceId = combined.Id });
        }
        return new(evidence, quotes, limitation is null ? "Succeeded" : "Partial", limitation);
    }

    private async Task<SourceResult> TwelveDataAsync(string endpoint, Instrument instrument, DateTimeOffset cutoff, CancellationToken cancellation)
    {
        if (!options.TwelveDataRights || string.IsNullOrWhiteSpace(options.TwelveDataApiKey) || !options.TwelveDataEntitlements.Contains(endpoint))
            return Unavailable("TwelveDataRequiresSecureKeyRightsAndEndpointEntitlement");
        if (!options.TwelveDataEvidenceRetentionAuthorized) return Unavailable("TwelveDataEvidenceRetentionNotAuthorized");
        if (options.TwelveDataRequestCredits.Diagnostic(endpoint) is { } creditDiagnostic) return Unavailable(creditDiagnostic);
        if (creditStore is null) return Unavailable("TwelveDataSharedCreditStoreMissing");
        var parameters = new Dictionary<string, string> { ["symbol"] = instrument.Symbol, ["mic_code"] = instrument.Exchange, ["format"] = "JSON" };
        if (endpoint == "eod") parameters["date"] = cutoff.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (endpoint == "quote") { parameters["eod"] = "true"; parameters["timezone"] = "UTC"; }
        if (endpoint == "time_series")
        {
            parameters["interval"] = "1day"; parameters["adjust"] = "none"; parameters["outputsize"] = "30";
            parameters["end_date"] = cutoff.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (endpoint == "press_releases") { parameters["end_date"] = cutoff.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); parameters["timezone"] = "UTC"; parameters["outputsize"] = "10"; }
        var uri = new Uri($"https://api.twelvedata.com/{endpoint}?" + string.Join('&', parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        var reservationId = Guid.NewGuid();
        SourceCreditAdmission admission;
        try
        {
            admission = await creditStore.ReserveAsync(reservationId, endpoint, options.TwelveDataRequestCredits.EndpointWeights[endpoint],
                options.TwelveDataRequestCredits.DailyMaximumCredits!.Value, options.TwelveDataRequestCredits.RateCardVersion!, cancellation);
        }
        catch (Exception error) when (error is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        { return Unavailable("TwelveDataSharedCreditStoreUnavailable"); }
        if (admission != SourceCreditAdmission.Reserved)
            return Unavailable(admission == SourceCreditAdmission.Exhausted ? "TwelveDataDailyCreditBudgetExhausted" : "TwelveDataCreditReservationAlreadyUsed");
        cancellation.ThrowIfCancellationRequested();
        var creditProvenance = $"reservationId={reservationId}; configuredCreditWeight={options.TwelveDataRequestCredits.EndpointWeights[endpoint]}; authorizedMaximumCreditsPerRequest={options.TwelveDataRequestCredits.MaximumCreditsPerRequest}; dailyMaximumCredits={options.TwelveDataRequestCredits.DailyMaximumCredits}; rateCard={options.TwelveDataRequestCredits.RateCardVersion}; ChargedUncertain retained; not provider-reported usage; credit-store UTC day, not evidence date";
        using var document = JsonDocument.Parse(await ResearchHttp.GetAsync(client, uri, null, options.TwelveDataApiKey, cancellation));
        var root = document.RootElement;
        if (root.TryGetProperty("status", out var status) && status.GetString() == "error") return Unavailable("TwelveDataDeniedOrUnavailable");
        var metadata = root.TryGetProperty("meta", out var meta) ? meta : root;
        if (endpoint != "press_releases") ValidateMetadata(metadata, instrument);
        var evidence = new List<EvidenceRecord>();
        var quotes = new List<Quote>();
        if (endpoint == "press_releases")
        {
            foreach (var release in root.GetProperty("press_releases").EnumerateArray().Take(10))
            {
                if (!TryTimestamp(ResearchHttp.Text(release, "datetime"), out var published)) continue;
                if (published > cutoff) continue;
                var title = ResearchHttp.Text(release, "title");
                if (title.Length > 1000) title = title[..1000];
                evidence.Add(Evidence(instrument, "TwelveData", uri.AbsoluteUri, "CorporateAnnouncement", published, title, [], options.TwelveDataModelProcessingAllowed,
                    $"press_releases/id={ResearchHttp.Text(release, "id")}; instrument matched by canonical query; title only; body/style HTML excluded; {creditProvenance}"));
            }
        }
        else
        {
            var values = endpoint == "time_series" ? root.GetProperty("values").EnumerateArray().Take(30).ToArray() : [root];
            foreach (var value in values)
            {
                DateTimeOffset published;
                if (endpoint == "quote" && value.TryGetProperty("timestamp", out var timestamp)) published = DateTimeOffset.FromUnixTimeSeconds(timestamp.GetInt64());
                else
                {
                    var date = ResearchHttp.Text(value, "datetime");
                    if (date.Length != 10) throw new SourceUnavailableException("UnsupportedBarTimestamp");
                    var localEnd = DateUtc(date).AddDays(1).UtcDateTime;
                    var timezoneName = metadata.TryGetProperty("exchange_timezone", out var timezone) ? timezone.GetString()
                        : options.ExchangeTimeZones.GetValueOrDefault(instrument.Exchange);
                    if (string.IsNullOrWhiteSpace(timezoneName)) return Unavailable("ExchangeTimezoneRequiredForDateOnlyPrice");
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(timezoneName);
                    published = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), zone));
                }
                if (published > cutoff) continue;
                var price = ResearchHttp.Number(value.GetProperty("close"));
                if (price <= 0) continue;
                var fact = Evidence(instrument, "TwelveData", uri.AbsoluteUri, "Price", published, "Dated close; date-only bars conservatively available at next exchange-local midnight. No executed trade price claim.",
                    new() { ["Close"] = price }, options.TwelveDataModelProcessingAllowed, $"endpoint={endpoint}; close; currency={instrument.Currency}; daily raw history adjust=none; quote eod=true; {creditProvenance}");
                evidence.Add(fact);
                quotes.Add(new(instrument.Id, price, instrument.Currency, instrument.Currency == "EUR" ? 1m : null, published, fact.Id));
            }
        }
        return Result(evidence, quotes, endpoint == "press_releases" ? "Corporate titles only, not comprehensive journalism; query identity, no issuer verification." : null);
    }

    private static void ValidateMetadata(JsonElement metadata, Instrument instrument)
    {
        if (ResearchHttp.Text(metadata, "symbol") != instrument.Symbol || ResearchHttp.Text(metadata, "mic_code") != instrument.Exchange
            || ResearchHttp.Text(metadata, "currency") != instrument.Currency) throw new SourceUnavailableException("TwelveDataIdentityMismatch");
    }
    private static string Cik(JsonElement element) => (element.ValueKind == JsonValueKind.Number ? element.GetInt64().ToString(CultureInfo.InvariantCulture) : element.GetString() ?? "").PadLeft(10, '0');
    private static DateTimeOffset DateUtc(string date) => new(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    private static DateTimeOffset DateEndInNewYork(string date)
    {
        var nextMidnight = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new(TimeZoneInfo.ConvertTimeToUtc(nextMidnight, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));
    }
    private static bool TryTimestamp(string? value, out DateTimeOffset timestamp) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    private static EvidenceRecord Evidence(Instrument instrument, string source, string url, string kind, DateTimeOffset published, string text,
        Dictionary<string, decimal> numbers, bool rights, string provenance, DateTimeOffset? period = null)
    {
        var identity = JsonDefaults.Serialize(new { instrument.Id, source, url, kind, published, numbers, provenance });
        var id = source.ToLowerInvariant() + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return new(id, instrument.Id, source, url, kind, published, DateTimeOffset.UtcNow, text, numbers, rights, provenance, period);
    }
}

public static class SyntheticResearchFixture
{
    public static readonly DateTimeOffset PublishedAt = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
    public static List<Instrument> Catalog => [new("SAMPLE:XNAS:USD", "SAMPLE", "XNAS", "USD", "Stock", "Synthetic Technology"), new("OTHER:XNAS:USD", "OTHER", "XNAS", "USD", "Stock", "Synthetic Technology")];

    public static SourceResult Fetch(string kind, Instrument instrument, DateTimeOffset cutoff)
    {
        if (!Catalog.Any(item => item.Id == instrument.Id && item.Symbol == instrument.Symbol && item.Exchange == instrument.Exchange
            && item.Currency == instrument.Currency && item.Kind == instrument.Kind && (item.Sector == instrument.Sector || item.Sector == $"Synthetic {instrument.Sector}")))
            return ResearchSources.Unavailable("NoSyntheticFixtureForInstrument");
        if (cutoff < PublishedAt) return ResearchSources.Unavailable("SyntheticFixtureNotPublishedByCutoff");
        var endpoint = ResearchSources.NormalizeKind(kind);
        var sample = instrument.Symbol == "SAMPLE";
        var numbers = endpoint switch
        {
            "companyfacts" => new Dictionary<string, decimal> { ["Revenue"] = sample ? 120_000_000m : 80_000_000m, ["NetIncome"] = sample ? 12_000_000m : 6_000_000m },
            "eod" or "quote" or "time_series" => new Dictionary<string, decimal> { ["Close"] = sample ? 120m : 45m, ["EurPerUnit"] = .9m },
            "fx" => new Dictionary<string, decimal> { ["EurPerUnit"] = .9m },
            "submissions" or "press_releases" => new Dictionary<string, decimal>(),
            _ => null
        };
        if (numbers is null) return ResearchSources.Unavailable("UnsupportedSyntheticField");
        var evidence = new EvidenceRecord($"synthetic-{instrument.Symbol.ToLowerInvariant()}-{endpoint}-20261001", instrument.Id, "SyntheticFixture",
            "fixture://pii/2026-10-01/" + instrument.Symbol, endpoint, PublishedAt, PublishedAt,
            endpoint is "submissions" or "press_releases" ? "Synthetic dated announcement: capacity expansion disclosed; execution and demand remain uncertain."
                : "Static synthetic facts for isolated Development tests, not market observations or user holdings.", numbers, true,
            "PII synthetic fixture v1; fixed independent facts; permitted for tests/model-on-fixtures; never live data", endpoint == "companyfacts" ? PublishedAt.AddDays(-1) : null);
        var quotes = endpoint is "eod" or "quote" or "time_series" ? new List<Quote> { new(instrument.Id, sample ? 120m : 45m, "USD", .9m, PublishedAt, evidence.Id) } : [];
        return new([evidence], quotes, "Succeeded");
    }
}
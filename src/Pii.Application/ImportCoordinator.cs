using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Pii.Domain;

namespace Pii.Application;

public sealed class ImportCoordinator(IWorkspaceStore store, IArtifactStore artifacts)
{
    public async Task<ImportPreview> PreviewAsync(Stream input, string filename, CancellationToken cancellation)
    {
        try { return await PreviewCoreAsync(input, filename, cancellation); }
        catch (Exception exception) when (exception is CsvHelperException or DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("Invalid CSV or normalized decimal ledger; UTF-8, exact headers, consistent columns and bounded amounts required.", exception);
        }
    }

    private async Task<ImportPreview> PreviewCoreAsync(Stream input, string filename, CancellationToken cancellation)
    {
        if (!filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only neutral PII sample trade/snapshot CSV is supported. Revolut CSV/PDF and scanned/encrypted statements are unverified; supply anonymized samples for a verified parser.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            if (bytes.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Upload exceeds 2 MiB.");
            bytes.Write(buffer, 0, read);
        }
        var raw = bytes.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        var baseline = await store.GetPortfolioAsync(cancellation);
        var previewId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{hash}|{baseline.Revision}")))[..32];
        var existing = await store.GetPreviewAsync(previewId, cancellation);
        if (existing is not null) return existing;
        var ledger = baseline.Transactions.ToList();
        var instruments = baseline.Instruments.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var issues = new List<string>();
        var duplicateCount = 0;
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(new MemoryStream(raw), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            { TrimOptions = TrimOptions.Trim, DetectColumnCountChanges = true, ExceptionMessagesContainRawData = false });
        if (!await csv.ReadAsync()) throw new InvalidDataException("Empty CSV.");
        csv.ReadHeader();
        string[] headers = ["id", "instrumentId", "symbol", "exchange", "currency", "kind", "sector", "time", "type", "quantity", "gross", "fee", "eurPerUnit", "cik", "splitRatio"];
        string[] snapshotHeaders = ["asOf", "recordType", "instrumentId", "currency", "quantity", "cashBalance"];
        if (csv.HeaderRecord?.SequenceEqual(snapshotHeaders) == true)
            return await PreviewSnapshotAsync(csv, baseline, previewId, hash, raw, cancellation);
        var cashHeader = csv.HeaderRecord?.SequenceEqual(headers.Append("cashLedgerComplete")) == true;
        if (!cashHeader && csv.HeaderRecord?.SequenceEqual(headers) != true)
            throw new InvalidDataException("PII sample trade or snapshot exact header required. This is not a Revolut parser.");
        var rows = 0;
        while (await csv.ReadAsync())
        {
            cancellation.ThrowIfCancellationRequested();
            if (++rows > 10000) throw new InvalidDataException("More than 10000 rows.");
            string Field(string name) => csv.GetField(name) ?? "";
            decimal Number(string name) => decimal.Parse(Field(name), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            decimal? Optional(string name) => Field(name).Length == 0 ? null : Number(name);
            try
            {
                var instrumentId = Field("instrumentId");
                var symbol = Field("symbol");
                var currency = Field("currency");
                var exchange = Field("exchange");
                ValidateCurrency(currency);
                if (!Enum.TryParse<TransactionKind>(Field("type"), false, out var kind) || !Enum.IsDefined(kind)
                    || Field("type") != kind.ToString()) throw new InvalidDataException("Unsupported named transaction type; numeric enum values are not accepted.");
                Instrument? instrument = null;
                if (instrumentId.Length == 0)
                {
                    if (kind is TransactionKind.Buy or TransactionKind.Sell or TransactionKind.Opening or TransactionKind.Split
                        || new[] { symbol, exchange, Field("kind"), Field("sector"), Field("cik") }.Any(item => item.Length > 0))
                        throw new InvalidDataException("Cash-only rows require blank instrument metadata; holdings/trades require a canonical instrument.");
                }
                else
                {
                    if (kind is TransactionKind.OpeningCash or TransactionKind.Deposit or TransactionKind.Withdrawal or TransactionKind.Fee
                        || instrumentId.Length > 128 || symbol.Length is 0 or > 32 || exchange.Length == 0
                        || instrumentId != $"{symbol}:{exchange}:{currency}" || instrumentId.Any(char.IsControl))
                        throw new InvalidDataException("Canonical symbol:exchange:currency identity required; account cash rows cannot invent an instrument.");
                    instrument = new(instrumentId, symbol, exchange, currency, Field("kind"), Field("sector"), Field("cik").Length == 0 ? null : Field("cik"));
                    if (instruments.TryGetValue(instrumentId, out var priorInstrument) && priorInstrument != instrument)
                        throw new InvalidDataException("Instrument metadata conflicts with the baseline.");
                }
                var time = ParseTime(Field("time"));
                if (time > DateTimeOffset.UtcNow) throw new InvalidDataException("Future transaction.");
                var sourceId = Field("id");
                var completeText = cashHeader ? Field("cashLedgerComplete") : "";
                if (completeText is not ("" or "true" or "false")) throw new InvalidDataException("cashLedgerComplete must be blank, true or false.");
                var transaction = new Transaction(sourceId, instrumentId, time, kind, Number("quantity"), Number("gross"), Number("fee"), currency, Optional("eurPerUnit"),
                    $"PII-Sample-v1:{hash}:row:{rows + 1}", Optional("splitRatio"), completeText == "true");
                if (transaction.Quantity < 0 || transaction.Gross < 0 || transaction.Fee < 0 || transaction.EurPerUnit is <= 0
                    || kind is TransactionKind.Buy or TransactionKind.Sell or TransactionKind.Opening && transaction.Quantity <= 0
                    || kind == TransactionKind.Split && transaction.SplitRatio is not > 0
                    || kind != TransactionKind.Split && transaction.SplitRatio is not null
                    || kind is TransactionKind.Sell or TransactionKind.Dividend or TransactionKind.Deposit && transaction.Fee > transaction.Gross
                    || kind is not (TransactionKind.Buy or TransactionKind.Sell or TransactionKind.Opening) && transaction.Quantity != 0
                    || kind is TransactionKind.Opening or TransactionKind.Split && (transaction.Gross != 0 || transaction.Fee != 0)
                    || kind == TransactionKind.OpeningCash && transaction.Fee != 0
                    || transaction.CashLedgerComplete && kind != TransactionKind.OpeningCash)
                    throw new InvalidDataException("Invalid nonnegative amounts, FX or quantity.");
                var fingerprint = Fingerprint(transaction);
                if (sourceId.Length == 0)
                {
                    var ordinal = occurrences.GetValueOrDefault(fingerprint) + 1;
                    occurrences[fingerprint] = ordinal;
                    sourceId = $"occ:{fingerprint}:{ordinal}";
                }
                else
                {
                    if (sourceId.Length > 128 || sourceId.Any(char.IsControl) || !sourceIds.Add(sourceId))
                        throw new InvalidDataException("Repeated or invalid stable source ID inside one file.");
                    sourceId = $"source:{sourceId}";
                }
                transaction = transaction with { Id = sourceId };
                var existingTransaction = ledger.FirstOrDefault(item => item.Id == sourceId);
                if (existingTransaction is not null)
                {
                    if (Fingerprint(existingTransaction) != fingerprint) issues.Add($"Row {rows + 1}: economic fields conflict with stable identity {sourceId}; confirmation blocked.");
                    else duplicateCount++;
                }
                else
                {
                    ledger.Add(transaction);
                    if (instrument is not null) instruments[instrumentId] = instrument;
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or InvalidDataException)
            {
                issues.Add($"Row {rows + 1}: {exception.Message}");
            }
        }
        if (rows == 0) issues.Add("No transaction rows.");
        if (ledger.Count > 10000) issues.Add("Ledger exceeds 10000 events; confirmation blocked.");
        var facts = new PortfolioReconstructor().Calculate(baseline.Revision + 1, ledger, [], DateTimeOffset.UtcNow);
        issues.AddRange(facts.Holdings.SelectMany(item => item.Issues).Where(item => item.StartsWith("Ambiguous", StringComparison.Ordinal) || item.StartsWith("Oversell", StringComparison.Ordinal)));
        var blob = await artifacts.WriteAsync($"originals/{hash}.csv", raw, "text/csv", cancellation);
        var preview = new ImportPreview(previewId, baseline.Revision, hash, blob, ledger, instruments.Values.ToList(), issues, duplicateCount, DateTimeOffset.UtcNow,
            cashHeader ? "PII-Sample-v1 + cashLedgerComplete (neutral sample, not verified Revolut)" : "PII-Sample-v1 (not verified Revolut)");
        await store.SavePreviewAsync(preview, cancellation);
        return preview;
    }

    private async Task<ImportPreview> PreviewSnapshotAsync(CsvReader csv, PortfolioRevision baseline, string previewId,
        string hash, byte[] raw, CancellationToken cancellation)
    {
        var issues = new List<string>();
        var holdings = new List<SnapshotHolding>();
        var cash = new List<SnapshotCash>();
        var instrumentIds = new HashSet<string>(StringComparer.Ordinal);
        var cashCurrencies = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? asOf = null;
        var rows = 0;
        while (await csv.ReadAsync())
        {
            cancellation.ThrowIfCancellationRequested();
            if (++rows > 10000) throw new InvalidDataException("More than 10000 snapshot rows.");
            string Field(string name) => csv.GetField(name) ?? "";
            try
            {
                var time = ParseTime(Field("asOf"));
                if (time > DateTimeOffset.UtcNow) throw new InvalidDataException("Future snapshot.");
                if (asOf is not null && asOf != time) throw new InvalidDataException("Every snapshot row must describe the same explicit as-of instant.");
                var currency = Field("currency");
                ValidateCurrency(currency);
                var instrumentId = Field("instrumentId");
                var source = $"PII-Snapshot-v1:{hash}:row:{rows + 1}";
                if (Field("recordType") == "Holding")
                {
                    var identity = instrumentId.Split(':');
                    if (identity.Length != 3 || identity[0].Length is 0 or > 32 || identity[1].Length == 0 || identity[2] != currency
                        || instrumentId.Length > 128 || instrumentId.Any(char.IsControl) || Field("cashBalance").Length != 0)
                        throw new InvalidDataException("Holding requires canonical symbol:exchange:currency and blank cashBalance.");
                    var quantity = decimal.Parse(Field("quantity"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                    if (!instrumentIds.Add(instrumentId)) throw new InvalidDataException("Repeated snapshot instrument.");
                    holdings.Add(new(instrumentId, currency, quantity, source));
                }
                else if (Field("recordType") == "Cash")
                {
                    if (instrumentId.Length != 0 || Field("quantity").Length != 0) throw new InvalidDataException("Cash requires blank instrumentId and quantity.");
                    var balance = decimal.Parse(Field("cashBalance"), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    if (!cashCurrencies.Add(currency)) throw new InvalidDataException("Repeated native cash currency; multiple accounts are unsupported.");
                    cash.Add(new(currency, balance, source));
                }
                else throw new InvalidDataException("Snapshot recordType must be Holding or Cash.");
                asOf = time;
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or InvalidDataException)
            {
                issues.Add($"Row {rows + 1}: {exception.Message}");
            }
        }
        if (rows == 0) issues.Add("No snapshot rows.");
        var snapshot = asOf is null ? null : new StatementSnapshot(asOf.Value, holdings, cash, $"PII-Snapshot-v1:{hash}");
        var reconciliation = snapshot is null ? null : new StatementReconciliation().Compare(baseline, snapshot);
        if (reconciliation is not null)
            issues.AddRange(reconciliation.Items.Where(item => item.Status == ReconciliationStatus.Mismatch)
                .Select(item => $"Snapshot mismatch {item.Metric} {item.InstrumentId} {item.Currency}: {item.Reason} Confirmation blocked."));
        var blob = await artifacts.WriteAsync($"originals/{hash}.csv", raw, "text/csv", cancellation);
        var preview = new ImportPreview(previewId, baseline.Revision, hash, blob, baseline.Transactions.ToList(), baseline.Instruments.ToList(),
            issues, 0, DateTimeOffset.UtcNow, "PII-Snapshot-v1 (neutral sample, not verified Revolut)", snapshot, reconciliation);
        await store.SavePreviewAsync(preview, cancellation);
        return preview;
    }

    private static void ValidateCurrency(string currency)
    {
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) throw new InvalidDataException("Three uppercase ASCII currency letters required.");
    }

    private static DateTimeOffset ParseTime(string text)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            throw new InvalidDataException("ISO timestamp with seconds and explicit timezone required; ordering is not guessed.");
        return time;
    }

    private static string Fingerprint(Transaction transaction)
    {
        var economicFields = JsonDefaults.Serialize(new { transaction.InstrumentId, transaction.Time, transaction.Kind, transaction.Quantity, transaction.Gross,
            transaction.Fee, transaction.Currency, transaction.EurPerUnit, transaction.SplitRatio });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(economicFields + (transaction.CashLedgerComplete ? "|CashLedgerComplete:true" : ""))));
    }
}
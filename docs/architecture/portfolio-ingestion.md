# Portfolio Ingestion

[ImportCoordinator](../../src/Pii.Application/ImportCoordinator.cs) supports **neutral PII sample trade/cash and independent snapshot CSV**, not verified Revolut exports. Real transaction CSV, statement PDF, scanned/encrypted documents and broker-specific layouts remain unverified/unsupported. No real broker sample has been supplied. The sample schemas below test deterministic behavior, not real ownership, export compatibility or account accuracy.

## Trade And Native Cash Samples

PII-Sample-v1 retains its original exact ordered header, UTF-8, invariant decimal values and explicit ISO timestamp with seconds and `Z` or numeric timezone offset:

```csv
id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio
```

The additive cash format has the same header with exactly one final column, `cashLedgerComplete`. Download it from the Imports page's native cash sample link. This entire example is synthetic and can be imported into an isolated empty fixture workspace:

```csv
id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio,cashLedgerComplete
open,,,,USD,,,2025-12-31T10:00:00Z,OpeningCash,0,2000,0,,,,true
buy,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Buy,10,1000,2,0.9,,,
sell,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-02T10:00:00Z,Sell,2,240,1,0.92,,,
```

Canonical instrument ID is `symbol:exchange:currency`. Currency validation is three uppercase ASCII letters, not verification of broker-account eligibility. Named, case-sensitive types are Buy, Sell, Opening, Split, Dividend, Withholding, Deposit, Withdrawal, Fee and OpeningCash; numeric enum values are rejected. Buy/Sell/Opening require positive quantity and an instrument. Opening holdings have zero gross/fee and **unknown acquisition basis**, never a zero-cost purchase. Splits require a positive ratio and zero quantity/gross/fee; other types must not carry a split ratio.

Account-only OpeningCash/Deposit/Withdrawal/Fee rows require blank instrument metadata and zero quantity. Dividend/Withholding can identify a canonical instrument or have blank instrument metadata; neither creates shares. Amounts are nonnegative magnitudes. Sales/dividends/deposits cannot have fees exceeding gross. Fee gross denotes a separate charge, not another copy of an embedded transaction fee.

`OpeningCash` gross is an explicit native opening balance, before any same-timestamp movements; it has zero fee. `cashLedgerComplete` accepts blank, `false` or `true`, with `true` allowed only on OpeningCash. A single opening per currency **plus an explicit declaration that all cash events from that opening through the calculation cutoff are present** is necessary for a known balance. The legacy header cannot declare completeness, so even its OpeningCash does not make cash known. Deposits, buys and observed movements alone never imply a zero opening. Multiple openings or absent completeness produce `CashUnknown`, not a guessed reset. This minimal format supports one declared native account per currency; it cannot pool multiple real accounts, model transfers/FX legs, or establish actual export continuity.

Native cash movements: buy `-(gross + fee)`; sale/dividend/deposit `gross - fee`; withholding/withdrawal/separate fee `-(gross + fee)`. Opening holdings and splits do not move cash. Cash before an explicit opening is not backfilled. The declaration is source/user evidence, not automatically verified coverage. EUR-valued cash uses identity FX for EUR; other currencies require the latest attributed quote FX whose date is on or before `asOf`. Historical acquisition/sale FX is not reused as current cash valuation FX. Missing valuation FX leaves the native balance known and valuation Partial.

## Independent Snapshot Sample

PII-Snapshot-v1 is dispatched solely by this distinct exact ordered header. Download it from the Imports page's independent snapshot sample link:

```csv
asOf,recordType,instrumentId,currency,quantity,cashBalance
2026-01-03T12:00:00+02:00,Holding,SAMPLE:XNAS:USD,USD,8,
2026-01-03T12:00:00+02:00,Cash,,USD,,1237
```

Every row must describe the same explicitly zoned instant; the supplied offset is retained separately from upload time. `Holding` requires a canonical ID, a nonnegative quantity and blank cashBalance. `Cash` requires blank instrumentId/quantity and an explicit signed native balance. Only one holding row per instrument and one cash row per currency are allowed. Future timestamps, mixed instants, duplicate rows, malformed decimals, unsupported types and nonconforming headers/column counts cannot be confirmed. No omitted balance or quantity is interpreted as zero.

[StatementReconciliation](../../src/Pii.Application/StatementReconciliation.cs) replays the existing deterministic ledger **only through the snapshot instant**, then compares independent native quantities/currencies and cash. A snapshot preview preserves the entire baseline ledger and instrument catalog unchanged, including later events; those later events do not enter the dated comparison. Snapshot rows are observations, not Opening transactions or cash-ledger declarations.

- `Match`: equal observed native quantity/balance with sufficient ledger evidence. A quantity match does not verify FIFO basis; a native cash match does not verify EUR valuation.
- `Mismatch`: unequal known native quantities/balances or conflicting holding currency. Mismatches become blocking `ImportPreview.Issues`, enforced by the existing store confirmation gate and the web handler.
- `Unknown`: missing opening/completeness, missing instrument quantity history, oversells, dividend-only history, or absent independent observations. Unknown is not a match and does not become a blocking parser error; the Imports confirmation checkbox is required both in HTML and server validation and explicitly acknowledges unverified coverage. There is no implicit account or opening zero.

For the paired synthetic files above, the January 3 observation is 8 units and USD 1,237 (`2000 - 1002 + 239`). Remaining economic basis is EUR 721.44 and realized economic FIFO gain EUR 39.52 for the supplied acquisition/sale FX. With a separately supplied dated USD 120 price and 0.9 EUR/USD quote, invested value is EUR 864; that price is not imported or invented by these files. Different samples/FX inputs can legitimately yield different gain values. Missing current FX keeps cash valuation Partial. These are independently hand-calculated fixture expectations, not current market facts.

## Bounds, Confirmation And Persistence

Upload maximum 2 MiB and 10,000 rows; a proposed ledger is bounded to 10,000 events. Originals retain SHA-256, row provenance, parser format and baseline revision privately. Exact file/baseline hash identifies a repeat preview. Stable source IDs deduplicate economic fields and conflict on differences, including a changed completeness declaration. Existing transaction fingerprints remain unchanged when the additive cash declaration is absent/false. Missing IDs preserve occurrence multiplicity, including two genuinely identical trades. This is a sample-format matching policy, not verified cross-format/partial-export broker matching. Ambiguous economic ordering, oversells and parser/conflict/mismatch issues block confirmation.

Preview stores a proposed ledger and private immutable original but does not move the active revision. Confirmation checks the expected baseline, rejects stale input, and in one serializable SQL transaction publishes a revision, active head, ledger identities, confirmed-preview pointer and pending analysis run. Repeated confirmation returns its committed revision. Pending work does not depend on the HTTP process staying alive.

**Adjacent persistence integration still required:** compatible optional `Snapshot`/`Reconciliation` fields exist on ImportPreview and PortfolioRevision. Preview JSON already retains them, but the current SQL confirmation constructor omits them. Its owner must append `Snapshot: preview.Snapshot, Reconciliation: preview.Reconciliation` to the PortfolioRevision creation in `SqlWorkspaceStore.ConfirmAsync`, then test confirmation/reload/run pinning and stale/mismatch gates. This is a JSON payload change, not a new SQL table. Until integrated, the Imports comparison works durably on the preview but the Portfolio page correctly reports no retained snapshot for the confirmed revision. Existing revisions without these fields remain readable. Do not claim completed durable revision linkage or live-source reconciliation before that integration and its tests.

FIFO uses decimal, partial lots, buy fees in basis, sale fees in proceeds, separate gross dividend income, withholding and known fee subtotals. Every holdings/cash/income/withholding/fee calculation excludes transactions after `asOf`; quotes/valuation FX also respect that cutoff. Unknown history/basis/FX is retained as missing coverage, not fabricated inputs. Statement-specific gross/net conventions, fees in another currency, corrections, unsettled cash and unsupported corporate actions still need real sample verification. P&L/value change is not TWR/MWR, weekly return, annual tax or Lithuanian tax compliance. Known invested and cash subtotals are separate, not automatically a full account total.

Provide privately anonymized overlapping exports plus an independent dated snapshot; preserve coherent dates/currencies/fees/quantities and stable pseudonyms, irreversibly remove personal identifiers and hidden PDF text. Do not commit real statements or submit them to an LLM. Verify expected quantities, cash, fees, dividends, FIFO and FX independently before expanding the parser's claimed formats. [Requirement trace](../product/requirements.md).
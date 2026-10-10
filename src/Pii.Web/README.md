# Private Web Host

Razor Pages (.NET 10), not an SPA. All workspace pages, CSV sample and original/report downloads require the single authenticated owner policy. No model adapter is registered in the web host. All financial reconstruction uses the Domain service; all ingestion/submission uses Application coordinators and durable Infrastructure ports.

## Development

Run from the repository: `dotnet run --project src/Pii.Web`.

The launch profile sets Development and `http://127.0.0.1:5080`. Development settings explicitly select `Pii:Mode=FixtureReplay` and `Pii:AllowDevelopmentIdentity=true`. Both actual remote peer and Host must be loopback; forwarded/original proxy headers are rejected. Arbitrary EasyAuth or user identity headers are never trusted. This bypass cannot be enabled in Production or Staging.

Infrastructure defaults: SQL LocalDB `MSSQLLocalDB`, database `PiiDevelopment`; Azurite `UseDevelopmentStorage=true`, private container `pii-documents`. Override with `Pii:Sql:ConnectionString`, `Pii:Blob:ConnectionString`, `Pii:Blob:Container`. The web and worker must use the same isolated Development database/container. No accepted import automatically inserts fake production holdings.

Download the authenticated sample from Imports. Exact headers:

```text
id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio
```

PII-Sample-v1 is an explicitly synthetic normalized schema, not verified Revolut CSV/PDF support. SAMPLE:XNAS:USD is held, OTHER:XNAS:USD is the outside research candidate in the static source catalog. Sample buy: 10 units, USD 1,000 gross + USD 2 fee, 0.9 EUR/USD, 2026-01-01T10:00Z. Sample sell: 2 units, USD 240 gross - USD 1 fee, 0.92 EUR/USD, 2026-01-02T10:00Z. Expected 8 units, EUR 721.44 known remaining basis, EUR 39.52 known realized economic FIFO gain. Fixture quotes are dated synthetic observations, never current prices. Independent holdings/account reconciliation and cash coverage are explicitly unavailable.

Upload is limited to 2 MiB; CSV extension, content type and parser schema are checked. Preview does not change the active ledger. Confirm requires antiforgery, baseline concurrency and explicit acknowledgement. Originals remain private in Blob storage. A preview is the complete candidate revision, not only incremental transactions.

## Production Entra

Configure outside source control, using environment variables (`:` becomes `__`) or protected platform configuration:

- `AzureAd:Instance=https://login.microsoftonline.com/`
- `AzureAd:TenantId`, `AzureAd:ClientId`, protected `AzureAd:ClientSecret`
- `AzureAd:CallbackPath=/signin-oidc` (default)
- `Pii:Auth:AllowedTenantId`, `Pii:Auth:AllowedObjectId` (nonempty GUIDs; tenant must match AzureAd)
- `AllowedHosts` with explicit production hostname(s), never `*`
- `Pii:DataProtection:BlobUri=https://<account>.blob.core.windows.net/<private-key-container>/data-protection.xml`
- Production SQL and Blob settings documented by Infrastructure; `Pii:Mode=Live`

Use Microsoft.Identity.Web 4.16.0, OpenID Connect and an HttpOnly, Secure, host-scoped session cookie. Token signature/issuer validation is performed by OIDC; authenticated `tid` and `oid` must match the owner allowlist on every page/download/mutation and at token validation. No EasyAuth header trust. Entra credential/configuration or owner allowlist missing means startup fails closed. Register an HTTPS redirect URI in Entra. Serve actual HTTPS at the app boundary; forwarded headers are not blindly trusted. If TLS is terminated upstream, deploy a reviewed trusted-proxy configuration rather than enabling arbitrary forwarded headers.

Production cookie keys are persisted with Azure.Extensions.AspNetCore.DataProtection.Blobs 1.5.4 and DefaultAzureCredential to the existing private Blob account. Provision the key container as private, restrict its RBAC to the web identity, and retain key-ring versions during deployments. Blob encryption at rest is used; no new Key Vault resource is implied. Production private-container/RBAC and Entra login are separately environment-verified, not proven by unit tests.

No JavaScript is required. CSP denies scripts; CSS/fonts for pinned lucide-static 1.55.0 are permitted from jsDelivr. Icon font failure leaves textual controls usable. No raw HTML rendering. No secrets, model prompts, original statements or exception payloads enter operational logging. Downloads whitelist server-owned original/report references; raw prompt artifacts are not exposed.

## Verification

`dotnet test tests/Pii.Web.Tests/Pii.Web.Tests.csproj` uses xUnit, WebApplicationFactory, test-only stores and AngleSharp. No live SQL/Azure is required for those HTTP tests. Coverage includes all private routes, forged headers, wrong owner, CSRF, escaped report text, import baseline and retry preserving old counters. Test transports are never registered in the production web project.
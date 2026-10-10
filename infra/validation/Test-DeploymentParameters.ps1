[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
$document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
if ($null -eq $document.parameters) { throw 'ARM parameter object required.' }
function Value([string]$Name, $Default = $null) {
    $parameter = $document.parameters.PSObject.Properties[$Name]
    if ($null -eq $parameter) { return $Default }
    if ($null -eq $parameter.Value.PSObject.Properties['value']) { throw "Explicit value required for $Name; use ACA Key Vault URI parameters for secret references." }
    return $parameter.Value.value
}
function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Deployment parameter gate: $Message" }
}
function Assert-Guid([string]$Name) {
    $parsed = [guid]::Empty
    Require ([guid]::TryParse([string](Value $Name), [ref]$parsed) -and $parsed -ne [guid]::Empty) "$Name must be a nonempty GUID"
}
function Assert-Positive([string]$Name, $Default = $null) {
    $parsed = [decimal]0
    Require ([decimal]::TryParse([string](Value $Name $Default), [Globalization.NumberStyles]::AllowDecimalPoint, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -and $parsed -gt 0) "$Name requires a positive invariant decimal"
}
function Assert-Integer([string]$Name, [long]$Minimum, [long]$Maximum, $Default = $null) {
    $value = Value $Name $Default
    Require (($value -is [int] -or $value -is [long]) -and $value -ge $Minimum -and $value -le $Maximum) "$Name requires a bounded JSON integer"
}
foreach ($name in @('tenantId', 'allowedObjectId', 'webClientId', 'sqlAdminObjectId')) { Assert-Guid $name }
Require ((Value 'prefix' 'pii') -cmatch '^[a-z][a-z0-9-]{2,15}$') 'prefix must be a lowercase Azure resource prefix'
Require ((Value 'location') -cmatch '^[a-z][a-z0-9]+$') 'explicit public Azure location required'
Require (-not [string]::IsNullOrWhiteSpace((Value 'sqlAdminName'))) 'SQL Entra administrator display name required'
Require ((Value 'databaseName' 'pii') -cmatch '^[A-Za-z][A-Za-z0-9_-]{0,62}$') 'database name must be safe for connection-string and SQLCMD use'
foreach ($name in @('deployWorkloads', 'grantRegistryPush', 'sqlAllowAzureServices', 'liveAuthorized', 'createFoundryAccount', 'createModelDeployment', 'modelProcessingAuthorized', 'modelSupportsStrictSchemaAndTools', 'secRightsAuthorized', 'secModelProcessingAllowed', 'secCompanyFactsLargerResponseAuthorized', 'ecbRightsAuthorized', 'ecbModelProcessingAllowed', 'twelveDataRightsAuthorized', 'twelveDataModelProcessingAllowed', 'twelveDataEvidenceRetentionAuthorized', 'twelveDataRequestCreditsAuthorized')) {
    Require ((Value $name $false) -is [bool]) "$name must be a JSON boolean, not a string"
}
if (Value 'grantRegistryPush' $false) { Assert-Guid 'deployPrincipalId' }
Require ((Value 'foundryResourceGroup' 'same-rg') -match '^[A-Za-z0-9_.()-]+$') 'Foundry resource group name required'
if ((Value 'createFoundryAccount' $false) -or (Value 'createModelDeployment' $false) -or (Value 'liveAuthorized' $false)) {
    Require ((Value 'foundryAccountName') -cmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{1,62}$') 'explicit Azure OpenAI account binding required'
}
if (Value 'createModelDeployment' $false) {
    foreach ($name in @('modelDeploymentName', 'modelName', 'modelVersion')) { Require (-not [string]::IsNullOrWhiteSpace((Value $name))) "$name required before deployment creation" }
    Require ((Value 'modelDeploymentSku' 'Standard') -in @('Standard', 'GlobalStandard', 'DataZoneStandard')) 'consumption model SKU only'
}
if (Value 'liveAuthorized' $false) {
    Require ((Value 'modelProcessingAuthorized' $false) -and (Value 'modelSupportsStrictSchemaAndTools' $false)) 'live inference requires explicit processing rights and verified strict tools capability'
    foreach ($name in @('modelDeploymentName', 'modelVersion', 'modelRateCardVersion', 'modelProcessingRegion')) { Require (-not [string]::IsNullOrWhiteSpace((Value $name))) "$name required for live inference" }
    Require ((Value 'modelDeploymentName') -cmatch '^[A-Za-z0-9_-]{1,128}$') 'valid inference deployment name'
    Assert-Positive 'modelInputUsdPerMillion'
    Assert-Positive 'modelOutputUsdPerMillion'
}
foreach ($pair in @(@('secModelProcessingAllowed', 'secRightsAuthorized'), @('ecbModelProcessingAllowed', 'ecbRightsAuthorized'), @('twelveDataModelProcessingAllowed', 'twelveDataRightsAuthorized'), @('twelveDataEvidenceRetentionAuthorized', 'twelveDataRightsAuthorized'), @('twelveDataRequestCreditsAuthorized', 'twelveDataRightsAuthorized'), @('secCompanyFactsLargerResponseAuthorized', 'secRightsAuthorized'))) {
    Require (-not (Value $pair[0] $false) -or (Value $pair[1] $false)) 'processing permission cannot bypass source rights'
}
Assert-Integer 'secCompanyFactsMaximumBytes' 1 8388608 1048576
Require ((Value 'secCompanyFactsMaximumBytes' 1048576) -le 1048576 -or (Value 'secCompanyFactsLargerResponseAuthorized' $false)) 'larger SEC companyfacts bound requires explicit approval'
Assert-Integer 'twelveDataMaximumCreditsPerRequest' 0 2147483647 0
Assert-Integer 'twelveDataDailyMaximumCredits' 0 2147483647 0
$weights = Value 'twelveDataEndpointWeights' ([pscustomobject]@{})
Require ($weights -is [pscustomobject]) 'endpoint weights require a JSON object'
foreach ($weight in $weights.PSObject.Properties) {
    Require ($weight.Name -cin @('eod', 'quote', 'time_series', 'press_releases')) 'unsupported endpoint credit weight'
    Require (($weight.Value -is [int] -or $weight.Value -is [long]) -and $weight.Value -gt 0 -and $weight.Value -le 2147483647) 'endpoint credit weights require positive JSON integers'
}
if (Value 'twelveDataRequestCreditsAuthorized' $false) {
    Assert-Integer 'twelveDataMaximumCreditsPerRequest' 1 2147483647
    Assert-Integer 'twelveDataDailyMaximumCredits' 1 2147483647
    $rate = Value 'twelveDataRateCardVersion' ''
    Require ($rate -is [string] -and -not [string]::IsNullOrWhiteSpace($rate) -and $rate.Length -le 128 -and $rate -notmatch '[\x00-\x1f\x7f]') 'reviewed rate-card version required'
    foreach ($endpoint in @(Value 'twelveDataEntitlements' @())) {
        $weight = $weights.PSObject.Properties | Where-Object { $_.Name -ceq $endpoint }
        Require ($null -ne $weight -and $weight.Value -le (Value 'twelveDataMaximumCreditsPerRequest')) 'each entitlement requires a known weight within per-request maximum'
    }
}
if (Value 'secRightsAuthorized' $false) {
    $agent = [string](Value 'secUserAgent' '')
    Require ($agent.Contains('@') -and $agent -notmatch '[\r\n]') 'SEC identified agent/contact required'
}
if (Value 'twelveDataRightsAuthorized' $false) {
    Require (-not [string]::IsNullOrWhiteSpace((Value 'twelveDataApiKey' '')) -or -not [string]::IsNullOrWhiteSpace((Value 'twelveDataApiKeyKeyVaultUri' ''))) 'Twelve Data requires a protected key or vault reference'
    Require ((Value 'twelveDataEvidenceRetentionAuthorized' $false) -and (Value 'twelveDataRequestCreditsAuthorized' $false)) 'Twelve Data requires indefinite evidence-retention permission and explicit daily/request-credit authorization'
    Require (@(Value 'twelveDataEntitlements' @()).Count -gt 0) 'Twelve Data requires explicit endpoint entitlements'
}
foreach ($name in @('webClientSecretKeyVaultUri', 'twelveDataApiKeyKeyVaultUri')) {
    $uri = [string](Value $name '')
    Require ([string]::IsNullOrEmpty($uri) -or $uri -cmatch '^https://[a-z0-9-]+\.vault\.azure\.net/secrets/[A-Za-z0-9-]+(/[A-Za-z0-9]+)?$') "$name must be an existing public-Azure secret URI"
}
$catalog = @(Value 'candidateCatalog' @())
Require ($catalog.Count -le 32) 'maximum 32 candidates'
$identifiers = @{}
foreach ($instrument in $catalog) {
    foreach ($field in @('Id', 'Symbol', 'Exchange', 'Currency', 'Kind', 'Sector')) { Require (-not [string]::IsNullOrWhiteSpace($instrument.$field)) "candidate $field required" }
    Require ($instrument.Symbol -cnotin @('SAMPLE', 'OTHER') -and $instrument.Sector -cnotmatch '^Synthetic') 'synthetic candidates are forbidden in production'
    Require ($instrument.Id -ceq "$($instrument.Symbol):$($instrument.Exchange):$($instrument.Currency)") 'canonical candidate identifier must match symbol:MIC:currency'
    Require ($instrument.Symbol -cmatch '^[A-Za-z0-9.-]{1,32}$' -and $instrument.Exchange -cmatch '^[A-Z0-9]{4}$' -and $instrument.Currency -cmatch '^[A-Z]{3}$') 'candidate symbol/MIC/currency invalid'
    Require ($instrument.Kind -cin @('Stock', 'ETF')) 'candidate kind must be Stock or ETF'
    Require (-not $identifiers.ContainsKey($instrument.Id)) 'duplicate candidate identity'
    $identifiers[$instrument.Id] = $true
}
foreach ($entitlement in @(Value 'twelveDataEntitlements' @())) { Require ($entitlement -cin @('eod', 'quote', 'time_series', 'press_releases')) 'unsupported Twelve Data endpoint entitlement' }
foreach ($name in @('perRunUsd', 'monthlyAiUsd', 'maximumCallUsd')) { Assert-Positive $name '1' }
if (Value 'deployWorkloads' $false) {
    Require (-not [string]::IsNullOrWhiteSpace((Value 'webClientSecret' '')) -or -not [string]::IsNullOrWhiteSpace((Value 'webClientSecretKeyVaultUri' ''))) 'protected Entra client credential required for web startup'
    foreach ($name in @('webImage', 'workerImage')) { Require ((Value $name) -cmatch '^[a-z0-9]+\.azurecr\.io/[a-z0-9/_-]+@sha256:[a-f0-9]{64}$') "$name must be an immutable private ACR digest reference" }
    Require ((Value 'sqlAllowAzureServices' $false) -or @(Value 'sqlFirewallRules' @()).Count -gt 0) 'explicit SQL network path required before workloads'
}
Write-Output 'Deployment parameter gates passed. Values and secrets suppressed; budget, authority, schema, grants and live compatibility still require independent approval/evidence.'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-PiiAz {
    param([string[]]$Arguments)
    $result = @(& az @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed ($LASTEXITCODE). Review local CLI diagnostics; do not publish secret-bearing output." }
    return ($result -join [Environment]::NewLine)
}

function Read-PiiManifest {
    [CmdletBinding()]
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'ManifestPath must explicitly select an existing manifest.' }
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -notin @(1,2)) { throw 'Unsupported manifest schemaVersion.' }
    $SubscriptionId = if ($manifest.PSObject.Properties.Name -contains 'subscriptionId') { $manifest.subscriptionId } else { '' }
    $parsedId = [guid]::Empty
    if ($SubscriptionId -isnot [string] -or -not [guid]::TryParse($SubscriptionId, [ref]$parsedId) -or $parsedId -eq [guid]::Empty) { throw 'Manifest subscriptionId must be a nonempty GUID.' }
    $manifest | Add-Member -NotePropertyName selection -NotePropertyValue ([pscustomobject]@{ manifestPath = (Resolve-Path -LiteralPath $Path).Path }) -Force
    foreach ($default in @{ prefix = 'pii'; environmentName = 'dev'; tags = [pscustomobject]@{} }.GetEnumerator()) {
        if ($null -eq $manifest.PSObject.Properties[$default.Key]) { $manifest | Add-Member $default.Key $default.Value }
    }
    foreach ($name in @('resourceGroupName', 'location', 'environmentName', 'prefix')) {
        if ($manifest.$name -notmatch '^[a-z][a-z0-9-]{1,39}$') { throw "Invalid manifest $name." }
    }
    if ($manifest.prefix -notmatch '^[a-z][a-z0-9-]{2,15}$' -or $manifest.environmentName.Length -gt 16) { throw 'Prefix/environment length exceeds stable deployment-name limits.' }
    foreach ($property in $manifest.stages.PSObject.Properties) {
        if ($property.Name -notin @('foundation','foundry','storage','data','monitoring','compute','application')) { throw 'Unknown manifest stage.' }
        if ($property.Value.enabled -isnot [bool]) { throw 'Stage enabled must be a boolean.' }
        if ($property.Value.PSObject.Properties.Name -contains 'resourceGroupName' -and $property.Value.resourceGroupName -notmatch '^[a-z][a-z0-9-]{1,39}$') { throw 'Invalid stage resourceGroupName.' }
        foreach ($name in @('location','prefix','environmentName','tags')) {
            $parameter = $property.Value.parameters.PSObject.Properties[$name]
            if ($null -ne $parameter -and (ConvertTo-Json -InputObject (Convert-PiiCanonicalValue $parameter.Value) -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject (Convert-PiiCanonicalValue $manifest.$name) -Depth 100 -Compress)) { throw "Conflicting configuration paths: $name and stages.$($property.Name).parameters.$name." }
        }
    }
    Resolve-PiiSharedConfiguration $manifest
    return $manifest
}

function Resolve-PiiSharedConfiguration {
    param($Configuration)
    $bindings = [ordered]@{
        'auth.tenantId' = @('data.tenantId','application.tenantId')
        'auth.allowedObjectId' = @('application.allowedObjectId')
        'auth.webAppClientId' = @('application.webClientId')
        'storage.accountName' = @('storage.accountName','compute.storageAccountName')
        'storage.documentsName' = @('storage.documentsName','compute.documentsName','application.documentsName')
        'storage.keysName' = @('storage.keysName','compute.keysName')
        'data.serverName' = @('data.serverName')
        'data.databaseName' = @('data.databaseName','application.databaseName')
        'foundry.accountName' = @('foundry.accountName','compute.foundryAccountName')
        'foundry.projectName' = @('foundry.projectName')
        'foundry.deploymentName' = @('foundry.deploymentName','application.modelDeploymentName')
        'foundry.modelVersion' = @('foundry.modelVersion','application.modelVersion')
        'compute.managedEnvironmentName' = @('compute.managedEnvironmentName','application.managedEnvironmentName')
        'compute.registryName' = @('compute.registryName','application.registryName')
        'compute.registryResourceGroup' = @('compute.registryResourceGroup','application.registryResourceGroup')
        'compute.webIdentityName' = @('compute.webIdentityName','application.webIdentityName')
        'compute.workerIdentityName' = @('compute.workerIdentityName','application.workerIdentityName')
        'compute.webName' = @('application.webName')
        'compute.workerName' = @('application.workerName')
        'monitoring.workspaceName' = @('monitoring.workspaceName','compute.workspaceName')
        'monitoring.workspaceResourceGroup' = @('monitoring.workspaceResourceGroup','compute.workspaceResourceGroup')
        'monitoring.insightsName' = @('monitoring.insightsName')
    }
    if ($Configuration.PSObject.Properties.Name -notcontains 'shared') { $Configuration | Add-Member shared ([pscustomobject]@{}) }
    if ($Configuration.shared -isnot [pscustomobject]) { throw 'shared must be an object.' }
    foreach ($group in $Configuration.shared.PSObject.Properties) {
        if ($group.Name -notin @('auth','storage','data','foundry','compute','monitoring') -or $group.Value -isnot [pscustomobject]) { throw "Unknown or invalid shared group: shared.$($group.Name)." }
        foreach ($field in $group.Value.PSObject.Properties) {
            if ("$($group.Name).$($field.Name)" -notin @($bindings.Keys) -and "$($group.Name).$($field.Name)" -notin @('storage.resourceGroupName','data.resourceGroupName','foundry.resourceGroupName','compute.resourceGroupName','monitoring.resourceGroupName')) {
                throw "Unknown shared field: shared.$($group.Name).$($field.Name). Secrets belong in external protected files."
            }
        }
    }
    foreach ($groupName in @('auth','storage','data','foundry','compute','monitoring')) {
        if ($null -eq $Configuration.shared.PSObject.Properties[$groupName]) { $Configuration.shared | Add-Member $groupName ([pscustomobject]@{}) }
    }
    foreach ($binding in $bindings.Keys) {
        $groupName, $fieldName = $binding.Split('.')
        $group = $Configuration.shared.$groupName
        $value = $null
        $source = "shared.$binding"
        if ($null -ne $group.PSObject.Properties[$fieldName]) {
            $value = $group.$fieldName
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) { throw "Nonempty string required: shared.$binding." }
        }
        foreach ($target in $bindings[$binding]) {
            $stageName, $parameterName = $target.Split('.')
            $stage = $Configuration.stages.PSObject.Properties[$stageName]
            if ($null -eq $stage) { continue }
            $parameters = $stage.Value.parameters
            if ($null -ne $parameters.PSObject.Properties[$parameterName]) {
                if ($null -eq $value) { $value = $parameters.$parameterName; $source = "stages.$stageName.parameters.$parameterName" }
                elseif ($value -cne $parameters.$parameterName) { throw "Conflicting configuration paths: $source and stages.$stageName.parameters.$parameterName." }
            }
        }
        if ($null -eq $value) { continue }
        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) { throw "Nonempty string required: $source." }
        $group | Add-Member $fieldName $value -Force
        foreach ($target in $bindings[$binding]) {
            $stageName, $parameterName = $target.Split('.')
            $stage = $Configuration.stages.PSObject.Properties[$stageName]
            if ($null -ne $stage) { $stage.Value.parameters | Add-Member $parameterName $value -Force }
        }
    }
    foreach ($ownerName in @('storage','data','foundry','compute','monitoring')) {
        $owner = $Configuration.stages.PSObject.Properties[$ownerName]
        $ownerSection = if ($null -eq $owner) { [pscustomobject]@{ enabled = $false; parameters = [pscustomobject]@{} } } else { $owner.Value }
        $group = $Configuration.shared.$ownerName
        $scope = $null
        $source = "shared.$ownerName.resourceGroupName"
        if ($null -ne $group.PSObject.Properties['resourceGroupName']) { $scope = $group.resourceGroupName }
        if ($null -ne $ownerSection.PSObject.Properties['existingResourceId']) {
            $types = @{ foundry = 'Microsoft.CognitiveServices/accounts'; storage = 'Microsoft.Storage/storageAccounts'; data = 'Microsoft.Sql/servers' }
            $identifier = $ownerSection.existingResourceId
            if (-not $types.ContainsKey($ownerName) -or $identifier -notmatch '^/subscriptions/([^/]+)/resourceGroups/([^/]+)/providers/([^/]+/[^/]+)/([^/]+)$') { throw "Invalid stages.$ownerName.existingResourceId." }
            if ($Matches[1] -ine $Configuration.subscriptionId -or $Matches[3] -ine $types[$ownerName]) { throw "Existing resource ID subscription/type mismatch: stages.$ownerName.existingResourceId." }
            $idScope = $Matches[2]
            $idName = $Matches[4]
            $nameKey = if ($ownerName -eq 'data') { 'serverName' } else { 'accountName' }
            if ($ownerSection.parameters.mode -cne 'existing' -or $null -eq $group.PSObject.Properties[$nameKey] -or $group.$nameKey -cne $idName) { throw "Existing ID must match explicit existing mode/name: stages.$ownerName.existingResourceId and shared.$ownerName.$nameKey." }
            if ($null -ne $scope -and $scope -cne $idScope) { throw "Conflicting configuration paths: $source and stages.$ownerName.existingResourceId." }
            $scope = $idScope
            $source = "stages.$ownerName.existingResourceId"
        }
        if ($null -ne $ownerSection.PSObject.Properties['resourceGroupName']) {
            if ($null -ne $scope -and $scope -cne $ownerSection.resourceGroupName) { throw "Conflicting configuration paths: $source and stages.$ownerName.resourceGroupName." }
            $scope = $ownerSection.resourceGroupName
        }
        $consumerKey = @{ storage = 'storageResourceGroup'; foundry = 'foundryResourceGroup' }[$ownerName]
        if ($null -eq $scope -and -not $ownerSection.enabled -and $consumerKey -and $null -ne $Configuration.stages.PSObject.Properties['compute']) {
            $consumerScope = $Configuration.stages.compute.parameters.PSObject.Properties[$consumerKey]
            if ($null -ne $consumerScope) { $scope = $consumerScope.Value }
        }
        if ($null -eq $scope) { $scope = $Configuration.resourceGroupName }
        if ($scope -notmatch '^[a-z][a-z0-9-]{1,39}$') { throw "Invalid shared.$ownerName.resourceGroupName." }
        $group | Add-Member resourceGroupName $scope -Force
        if ($null -ne $owner) { $ownerSection | Add-Member resourceGroupName $scope -Force }
        if ($consumerKey -and $Configuration.stages.PSObject.Properties.Name -contains 'compute') {
            $parameters = $Configuration.stages.compute.parameters
            if ($null -ne $parameters.PSObject.Properties[$consumerKey] -and $parameters.$consumerKey -cne $scope) { throw "Conflicting configuration paths: shared.$ownerName.resourceGroupName and stages.compute.parameters.$consumerKey." }
            $parameters | Add-Member $consumerKey $scope -Force
        }
    }
    foreach ($scopeBinding in @(@('compute','registryResourceGroup'), @('monitoring','workspaceResourceGroup'))) {
        $ownerName, $parameterName = $scopeBinding
        $group = $Configuration.shared.$ownerName
        if ($null -eq $group.PSObject.Properties[$parameterName] -and $null -ne $group.PSObject.Properties['resourceGroupName']) {
            $group | Add-Member $parameterName $group.resourceGroupName
            foreach ($target in $bindings["$ownerName.$parameterName"]) {
                $stageName, $targetName = $target.Split('.')
                if ($null -ne $Configuration.stages.PSObject.Properties[$stageName]) { $Configuration.stages.$stageName.parameters | Add-Member $targetName $group.resourceGroupName -Force }
            }
        }
    }
    if ($null -ne $Configuration.stages.PSObject.Properties['application'] -and $Configuration.stages.application.enabled -and $null -ne $Configuration.stages.PSObject.Properties['compute']) {
        $applicationGroup = $Configuration.resourceGroupName
        if ($null -ne $Configuration.stages.application.PSObject.Properties['resourceGroupName']) { $applicationGroup = $Configuration.stages.application.resourceGroupName }
        if ($applicationGroup -cne $Configuration.shared.compute.resourceGroupName) { throw 'Conflicting configuration paths: stages.application.resourceGroupName and shared.compute.resourceGroupName. Existing Bicep requires environment/identities in the application RG.' }
    }
    if ($Configuration.stages.PSObject.Properties.Name -contains 'application') {
        $parameters = $Configuration.stages.application.parameters
        $endpoints = @{}
        if ($null -ne $Configuration.shared.storage.PSObject.Properties['accountName']) {
            $endpoints.blobServiceUri = "https://$($Configuration.shared.storage.accountName).blob.core.windows.net/"
            if ($null -ne $Configuration.shared.storage.PSObject.Properties['keysName']) { $endpoints.keyRingUri = "https://$($Configuration.shared.storage.accountName).blob.core.windows.net/$($Configuration.shared.storage.keysName)/keyring.xml" }
        }
        if ($null -ne $Configuration.shared.data.PSObject.Properties['serverName']) { $endpoints.sqlServerFqdn = "$($Configuration.shared.data.serverName).database.windows.net" }
        foreach ($key in $endpoints.Keys) {
            if ($null -ne $parameters.PSObject.Properties[$key]) {
                $actual = $parameters.$key
                $valid = switch ($key) {
                    'blobServiceUri' { $actual.TrimEnd('/') -ceq $endpoints[$key].TrimEnd('/') }
                    'keyRingUri' { $actual -cmatch ('^https://' + [regex]::Escape($Configuration.shared.storage.accountName) + '\.blob\.core\.windows\.net/' + [regex]::Escape($Configuration.shared.storage.keysName) + '/[^\s?#]+\.xml$') }
                    default { $actual -ceq $endpoints[$key] }
                }
                if (-not $valid) { throw "Conflicting configuration paths: stages.application.parameters.$key and shared storage/data identifiers. Expected HTTPS account root / HTTPS XML blob path / selected SQL hostname." }
            } else { $parameters | Add-Member $key $endpoints[$key] }
        }
    }
}

function Get-PiiValue {
    param([hashtable]$Parameters, $Template, [string]$Name)
    if ($Parameters.ContainsKey($Name)) { return $Parameters[$Name].value }
    $property = $Template.parameters.PSObject.Properties[$Name]
    if ($null -ne $property -and $property.Value.PSObject.Properties.Name -contains 'defaultValue') {
        if ($Name -eq 'createProject' -and $property.Value.defaultValue -is [string]) { return (Get-PiiValue $Parameters $Template 'mode') -eq 'create' }
        return $property.Value.defaultValue
    }
    return $null
}

function Get-PiiHash {
    param([string]$Text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Convert-PiiCanonicalValue {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [array]) {
        $items = @()
        foreach ($item in $Value) { $items += ,(Convert-PiiCanonicalValue $item) }
        return ,$items
    }
    if ($Value -is [pscustomobject]) {
        $ordered = [ordered]@{}
        foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) { $ordered[$property.Name] = Convert-PiiCanonicalValue $property.Value }
        return $ordered
    }
    return $Value
}

function ConvertFrom-PiiWhatIfOutput {
    param([Parameter(Mandatory)][string]$Output)
    $normalized = ($Output -replace '\x1B\[[0-?]*[ -/]*[@-~]', '').TrimStart([char]0xFEFF)
    foreach ($match in [regex]::Matches($normalized, '(?m)^\s*\{')) {
        $start = $match.Index + $match.Value.LastIndexOf('{')
        $depth = 0
        $inString = $false
        $escaped = $false
        for ($index = $start; $index -lt $normalized.Length; $index++) {
            $character = $normalized[$index]
            if ($inString) {
                if ($escaped) { $escaped = $false }
                elseif ($character -eq '\') { $escaped = $true }
                elseif ($character -eq '"') { $inString = $false }
                continue
            }
            if ($character -eq '"') { $inString = $true; continue }
            if ($character -eq '{') { $depth++ }
            elseif ($character -eq '}') {
                $depth--
                if ($depth -eq 0) {
                    $json = $normalized.Substring($start, $index - $start + 1)
                    try { $response = $json | ConvertFrom-Json }
                    catch { break }
                    if ($null -ne $response.PSObject.Properties['changes']) { return $response.changes }
                    break
                }
            }
        }
    }
    throw 'Azure CLI WhatIf output did not contain a valid JSON document with a changes list.'
}

function Test-PiiParameters {
    param([hashtable]$Parameters, $Template, $Manifest, [string]$Stage)
    foreach ($key in $Parameters.Keys) {
        $definition = $Template.parameters.PSObject.Properties[$key]
        if ($null -eq $definition) { throw "Unknown $Stage parameter: $key." }
        $value = $Parameters[$key].value
        $type = $definition.Value.type
        $validType = switch ($type) {
            { $_ -in @('string','secureString') } { $value -is [string] }
            'bool' { $value -is [bool] }
            'int' { $value -is [int] -or $value -is [long] }
            'array' { $value -is [array] }
            { $_ -in @('object','secureObject') } { $value -is [pscustomobject] -or $value -is [hashtable] }
            default { $false }
        }
        if (-not $validType) { throw "Wrong type for $Stage parameter $key." }
        $constraints = $definition.Value.PSObject.Properties.Name
        if ($constraints -contains 'allowedValues' -and $definition.Value.allowedValues -cnotcontains $value) { throw "Unsupported value for $key." }
        if ($constraints -contains 'minValue' -and $value -lt $definition.Value.minValue) { throw "$key is below minimum." }
        if ($constraints -contains 'maxValue' -and $value -gt $definition.Value.maxValue) { throw "$key is above maximum." }
        if ($constraints -contains 'minLength' -and $value.Length -lt $definition.Value.minLength) { throw "$key is too short." }
        if ($constraints -contains 'maxLength' -and $value.Length -gt $definition.Value.maxLength) { throw "$key is too long." }
        if ($value -is [string] -and $value -match '^/subscriptions/([^/]+)/' -and $Matches[1] -cne $Manifest.subscriptionId) { throw 'Cross-subscription binding is unsupported.' }
    }
    $get = { param($name) Get-PiiValue $Parameters $Template $name }
    foreach ($key in @('accountName','storageAccountName','registryName','serverName','managedEnvironmentName','webIdentityName','workerIdentityName','webName','workerName','projectName','documentsName','keysName')) {
        if (-not $Parameters.ContainsKey($key)) { continue }
        $pattern = '^[a-zA-Z0-9][a-zA-Z0-9-]{1,62}$'
        if ($key -in @('storageAccountName','registryName') -or ($key -eq 'accountName' -and $Stage -eq 'storage')) { $pattern = '^[a-z0-9]{3,24}$' }
        if ((& $get $key) -cnotmatch $pattern -or ((& $get $key) -match '--')) { throw "Invalid resource name: $key." }
    }
    if ($Parameters.ContainsKey('databaseName') -and (& $get 'databaseName') -cnotmatch '^[A-Za-z][A-Za-z0-9_-]{0,62}$') { throw 'Unsafe database name for SQL connection/SQLCMD.' }
    foreach ($key in @('tenantId','adminObjectId','allowedObjectId','webClientId')) {
        if (-not $Parameters.ContainsKey($key)) { continue }
        $id = [guid]::Empty
        if (-not [guid]::TryParse([string](& $get $key), [ref]$id) -or $id -eq [guid]::Empty) { throw "Nonempty GUID required: $key." }
    }
    foreach ($name in @('location','prefix','environmentName','resourceGroupName')) {
        if ($Parameters.ContainsKey($name) -and $Parameters[$name].value -cne $Manifest.$name) { throw "Stage cannot override manifest $name." }
    }
    foreach ($name in @('developmentSharedKeyOptIn','developmentPasswordOptIn')) {
        if ((& $get $name) -and $Manifest.environmentName -cne 'dev') { throw "$name is development-only." }
    }
    if ($Stage -eq 'foundry') {
        if ((& $get 'mode') -eq 'existing' -and -not $Parameters.ContainsKey('accountName')) { throw 'Existing Foundry requires explicit accountName.' }
        if ((& $get 'mode') -eq 'existing' -and (& $get 'existingAccountKind') -eq 'OpenAI' -and ((& $get 'createProject') -or (& $get 'bindProject'))) { throw 'OpenAI accounts cannot create/bind modern projects.' }
        if ((& $get 'createModelDeployment')) {
            foreach ($name in @('deploymentName','modelName','modelVersion')) { if ([string]::IsNullOrWhiteSpace((& $get $name))) { throw "Model creation requires $name." } }
        }
        if (-not (& $get 'disableLocalAuth') -and $Manifest.environmentName -cne 'dev') { throw 'Foundry local authentication is development-only.' }
    }
    if ($Stage -eq 'storage') {
        if ((& $get 'documentsName') -ceq (& $get 'keysName')) { throw 'Document and key containers must differ.' }
        if ((& $get 'mode') -eq 'existing' -and -not $Parameters.ContainsKey('accountName')) { throw 'Existing storage requires explicit accountName.' }
    }
    if ($Stage -eq 'data') {
        if ((& $get 'computeModel') -eq 'Basic' -and (& $get 'maximumStorageGb') -gt 2) { throw 'Basic supports at most 2 GiB.' }
        if ([decimal](& $get 'minimumVcores') -gt (& $get 'maximumVcores')) { throw 'SQL minimum exceeds maximum vCores.' }
        if ((& $get 'mode') -eq 'existing' -and -not $Parameters.ContainsKey('serverName')) { throw 'Existing SQL requires explicit serverName.' }
        if ((& $get 'developmentPasswordOptIn') -and [string]::IsNullOrWhiteSpace((& $get 'sqlAdministratorPassword'))) { throw 'Password opt-in requires a secure password parameter.' }
    }
    if ($Stage -eq 'monitoring') {
        if ((& $get 'workspaceMode') -eq 'create' -and $Parameters.ContainsKey('workspaceResourceGroup') -and (& $get 'workspaceResourceGroup') -cne $Manifest.stages.monitoring.resourceGroupName) { throw 'New workspace must use deployment RG.' }
    }
    if ($Stage -in @('compute','application')) {
        if ((& $get 'registryMode') -eq 'create' -and $Parameters.ContainsKey('registryResourceGroup') -and (& $get 'registryResourceGroup') -cne $Manifest.stages.compute.resourceGroupName) { throw 'New registry must use deployment RG.' }
        if ((& $get 'monitoringEnabled') -and [string]::IsNullOrWhiteSpace((& $get 'workspaceName'))) { throw 'Monitoring needs explicit workspaceName.' }
        if ((& $get 'grantInference') -and [string]::IsNullOrWhiteSpace((& $get 'foundryAccountName'))) { throw 'Inference grant needs explicit foundryAccountName.' }
    }
    if ($Stage -eq 'application') {
        $positive = {
            param($name)
            $amount = [decimal]0
            if (-not [decimal]::TryParse([string](& $get $name), [Globalization.NumberStyles]::AllowDecimalPoint, [Globalization.CultureInfo]::InvariantCulture, [ref]$amount) -or $amount -le 0) { throw "Positive invariant decimal required: $name." }
        }
        foreach ($key in @('perRunUsd','monthlyAiUsd','maximumCallUsd')) { & $positive $key }
        if ((& $get 'liveAuthorized')) {
            if (-not (& $get 'modelProcessingAuthorized') -or -not (& $get 'modelSupportsStrictSchemaAndTools')) { throw 'Live requires explicit processing rights and verified strict tools capability.' }
            foreach ($key in @('modelEndpoint','modelDeploymentName','modelVersion','modelProcessingRegion','modelRateCardVersion')) { if ([string]::IsNullOrWhiteSpace((& $get $key))) { throw "Live requires $key." } }
            foreach ($key in @('modelInputUsdPerMillion','modelOutputUsdPerMillion')) { & $positive $key }
        }
        foreach ($pair in @(@('secModelProcessingAllowed','secRightsAuthorized'), @('ecbModelProcessingAllowed','ecbRightsAuthorized'), @('twelveDataModelProcessingAllowed','twelveDataRightsAuthorized'), @('twelveDataEvidenceRetentionAuthorized','twelveDataRightsAuthorized'), @('twelveDataRequestCreditsAuthorized','twelveDataRightsAuthorized'), @('secCompanyFactsLargerResponseAuthorized','secRightsAuthorized'))) {
            if ((& $get $pair[0]) -and -not (& $get $pair[1])) { throw 'Source processing/retention/credit approval cannot bypass source rights.' }
        }
        if ((& $get 'secCompanyFactsMaximumBytes') -gt 1048576 -and -not (& $get 'secCompanyFactsLargerResponseAuthorized')) { throw 'Larger SEC responses require separate approval.' }
        if ((& $get 'secRightsAuthorized') -and ((& $get 'secUserAgent') -notmatch '@' -or (& $get 'secUserAgent') -match '[\r\n]')) { throw 'SEC requires a protected identified contact.' }
        foreach ($key in @('webClientSecretKeyVaultUri','twelveDataApiKeyKeyVaultUri')) {
            $uri = & $get $key
            if ($uri -and $uri -cnotmatch '^https://[a-z0-9-]+\.vault\.azure\.net/secrets/[A-Za-z0-9-]+(/[A-Za-z0-9]+)?$') { throw 'Existing public Azure Key Vault secret URI required.' }
        }
        $allowedEndpoints = @('eod','quote','time_series','press_releases')
        foreach ($endpoint in @(& $get 'twelveDataEntitlements')) { if ($endpoint -cnotin $allowedEndpoints) { throw 'Unsupported provider entitlement.' } }
        $weights = & $get 'twelveDataEndpointWeights'
        foreach ($weight in $weights.PSObject.Properties) {
            if ($weight.Name -cnotin $allowedEndpoints -or ($weight.Value -isnot [int] -and $weight.Value -isnot [long]) -or $weight.Value -le 0 -or $weight.Value -gt [int]::MaxValue) { throw 'Reviewed supported positive integer endpoint weights required.' }
        }
        if ((& $get 'twelveDataRequestCreditsAuthorized')) {
            if ((& $get 'twelveDataMaximumCreditsPerRequest') -le 0 -or (& $get 'twelveDataDailyMaximumCredits') -le 0 -or [string]::IsNullOrWhiteSpace((& $get 'twelveDataRateCardVersion'))) { throw 'Explicit positive request/daily native credits and rate-card required.' }
            foreach ($endpoint in @(& $get 'twelveDataEntitlements')) {
                $weight = $weights.PSObject.Properties[$endpoint]
                if ($null -eq $weight -or $weight.Value -gt (& $get 'twelveDataMaximumCreditsPerRequest')) { throw 'Each entitlement needs a known bounded weight.' }
            }
        }
        if ((& $get 'twelveDataRightsAuthorized')) {
            if (-not (& $get 'twelveDataEvidenceRetentionAuthorized') -or -not (& $get 'twelveDataRequestCreditsAuthorized') -or @(& $get 'twelveDataEntitlements').Count -eq 0) { throw 'Licensed provider needs indefinite retention and explicit credit/entitlement approval.' }
            if ([string]::IsNullOrWhiteSpace((& $get 'twelveDataApiKey')) -and [string]::IsNullOrWhiteSpace((& $get 'twelveDataApiKeyKeyVaultUri'))) { throw 'Licensed provider requires protected worker key or vault URI.' }
        }
        $ids = @{}
        foreach ($instrument in @(& $get 'candidateCatalog')) {
            if ($instrument.Id -cne "$($instrument.Symbol):$($instrument.Exchange):$($instrument.Currency)" -or $instrument.Symbol -cnotmatch '^[A-Za-z0-9.-]{1,32}$' -or $instrument.Exchange -cnotmatch '^[A-Z0-9]{4}$' -or $instrument.Currency -cnotmatch '^[A-Z]{3}$' -or $instrument.Kind -cnotin @('Stock','ETF') -or [string]::IsNullOrWhiteSpace($instrument.Sector)) { throw 'Valid canonical candidate catalog required.' }
            if ($ids.ContainsKey($instrument.Id) -or $instrument.Symbol -cin @('SAMPLE','OTHER') -or $instrument.Sector -cmatch '^Synthetic') { throw 'Duplicate/synthetic production candidate rejected.' }
            $ids[$instrument.Id] = $true
        }
        if ((& $get 'webMinimumReplicas') -gt (& $get 'webMaximumReplicas')) { throw 'Minimum replicas exceed maximum.' }
        if ((& $get 'workerTimeoutSeconds') -lt ((& $get 'maximumRunMinutes') * 60 + 300)) { throw 'Worker timeout lacks startup allowance.' }
        if ((& $get 'scheduleEnabled') -and -not (& $get 'deployWorkloads')) { throw 'Scheduling requires workloads.' }
        if ((& $get 'workerCron') -notmatch '^(\S+\s+){4}\S+$') { throw 'Worker cron must have five UTC fields.' }
        if ((& $get 'deployWorkloads')) {
            foreach ($name in @('webImage','workerImage')) {
                $image = & $get $name
                if ($image -cnotmatch '^[a-z0-9]+\.azurecr\.io/[^\s@]+@sha256:[a-f0-9]{64}$') { throw 'Workloads require immutable private ACR image digests.' }
                if ($image.Split('/')[0] -cne "$(& $get 'registryName').azurecr.io") { throw 'Both workload image hosts must match the selected registry.' }
            }
            if ([string]::IsNullOrWhiteSpace((& $get 'webClientSecret')) -and [string]::IsNullOrWhiteSpace((& $get 'webClientSecretKeyVaultUri'))) { throw 'Workloads require secure Entra client secret or vault URI.' }
        }
        if ((& $get 'telemetryEnabled') -and [string]::IsNullOrWhiteSpace((& $get 'applicationInsightsConnectionString'))) { throw 'Telemetry opt-in requires protected connection configuration.' }
        $endpoint = & $get 'modelEndpoint'
        if ($endpoint -and $endpoint -notmatch '^https://[a-zA-Z0-9-]+\.openai\.azure\.com/?$') { throw 'Model endpoint must be the resource OpenAI root, not a project/API path.' }
        if ((& $get 'blobServiceUri') -cnotmatch '^https://[a-z0-9]+\.blob\.core\.windows\.net/?$') { throw 'Blob service URI must be an HTTPS account root without query or fragment.' }
        if ((& $get 'keyRingUri') -cnotmatch '^https://[a-z0-9]+\.blob\.core\.windows\.net/[a-z0-9-]+/[^\s?#]+\.xml$') { throw 'Key ring URI must be an HTTPS XML blob path without query or fragment.' }
        if ((& $get 'sqlServerFqdn') -notmatch '^[a-z0-9-]+\.database\.windows\.net$') { throw 'Azure SQL server FQDN required.' }
    }
}

function Get-PiiResource {
    param([string]$SubscriptionId, [string]$ResourceGroup, [string]$Type, [string]$Name, [string]$ApiVersion)
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.StartsWith('[')) { throw "Explicit name required for preflight: $Type." }
    $id = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/$Type/$Name"
    return ((Invoke-PiiAz -Arguments @('resource','show','--subscription',$SubscriptionId,'--ids',$id,'--api-version',$ApiVersion,'--output','json','--only-show-errors')) | ConvertFrom-Json)
}

function Test-PiiPreflight {
    param($Manifest, [string]$Stage, [hashtable]$Parameters, $Template)
    $subscription = $Manifest.subscriptionId
    $group = $Manifest.resourceGroupName
    $get = { param($name) Get-PiiValue $Parameters $Template $name }
    $exists = Invoke-PiiAz -Arguments @('group','exists','--subscription',$subscription,'--name',$group,'--output','json','--only-show-errors')
    if ($exists.Trim() -eq 'true') {
        $existingGroup = (Invoke-PiiAz -Arguments @('group','show','--subscription',$subscription,'--name',$group,'--output','json','--only-show-errors')) | ConvertFrom-Json
        if ($existingGroup.location -cne $Manifest.location) { throw 'Existing RG location mismatch; do not overwrite it.' }
        if ($Stage -eq 'foundation' -and (& $get 'createResourceGroup')) {
            foreach ($tag in $Manifest.tags.PSObject.Properties) {
                if ($existingGroup.tags.PSObject.Properties.Name -notcontains $tag.Name -or $existingGroup.tags.($tag.Name) -cne $tag.Value) { throw 'Existing RG tags differ; use createResourceGroup=false instead of overwriting tags.' }
            }
        }
    } elseif ($Stage -ne 'foundation' -or -not (& $get 'createResourceGroup')) { throw 'Required resource group does not exist.' }
    if ($exists.Trim() -eq 'true') {
        $inventory = @((Invoke-PiiAz -Arguments @('resource','list','--subscription',$subscription,'--resource-group',$group,'--output','json','--only-show-errors')) | ConvertFrom-Json)
        $owned = @()
        if ($Stage -eq 'foundry' -and (& $get 'mode') -eq 'create') { $owned += @{ type = 'Microsoft.CognitiveServices/accounts'; name = (& $get 'accountName'); kind = 'AIServices' } }
        if ($Stage -eq 'storage' -and (& $get 'mode') -eq 'create') { $owned += @{ type = 'Microsoft.Storage/storageAccounts'; name = (& $get 'accountName'); kind = 'StorageV2' } }
        if ($Stage -eq 'data' -and (& $get 'mode') -eq 'create') { $owned += @{ type = 'Microsoft.Sql/servers'; name = (& $get 'serverName') } }
        if ($Stage -eq 'compute') {
            if ((& $get 'registryMode') -eq 'create') { $owned += @{ type = 'Microsoft.ContainerRegistry/registries'; name = (& $get 'registryName') } }
            if ((& $get 'environmentMode') -eq 'create') { $owned += @{ type = 'Microsoft.App/managedEnvironments'; name = (& $get 'managedEnvironmentName') } }
        }
        foreach ($desired in $owned) {
            foreach ($resource in @($inventory | Where-Object { $_.type -ieq $desired.type -and $_.name -ceq $desired.name })) {
                if ($resource.location -cne $Manifest.location) { throw 'Owned resource location mismatch.' }
                if ($desired.ContainsKey('kind') -and $resource.kind -cne $desired.kind) { throw 'Account type conversion is forbidden; use the compatible existing binding.' }
            }
        }
    }
    $bindings = @()
    if ($Stage -eq 'foundry' -and (& $get 'mode') -eq 'existing') { $bindings += @{ type = 'Microsoft.CognitiveServices/accounts'; name = (& $get 'accountName'); api = '2025-06-01'; group = $group; kind = (& $get 'existingAccountKind') } }
    if ($Stage -eq 'storage' -and (& $get 'mode') -eq 'existing') { $bindings += @{ type = 'Microsoft.Storage/storageAccounts'; name = (& $get 'accountName'); api = '2023-05-01'; group = $group; kind = 'StorageV2' } }
    if ($Stage -eq 'data' -and (& $get 'mode') -eq 'existing') { $bindings += @{ type = 'Microsoft.Sql/servers'; name = (& $get 'serverName'); api = '2023-08-01'; group = $group } }
    if ($Stage -eq 'monitoring' -and (& $get 'enabled')) {
        if ((& $get 'workspaceMode') -eq 'existing') { $bindings += @{ type = 'Microsoft.OperationalInsights/workspaces'; name = (& $get 'workspaceName'); api = '2023-09-01'; group = (& $get 'workspaceResourceGroup') } }
        if ((& $get 'insightsMode') -eq 'existing') { $bindings += @{ type = 'Microsoft.Insights/components'; name = (& $get 'insightsName'); api = '2020-02-02'; group = $group } }
    }
    if ($Stage -in @('compute','application')) {
        if ($Stage -eq 'application' -or (& $get 'environmentMode') -eq 'existing') { $bindings += @{ type = 'Microsoft.App/managedEnvironments'; name = (& $get 'managedEnvironmentName'); api = '2024-03-01'; group = $group } }
        if ($Stage -eq 'application' -or (& $get 'registryMode') -eq 'existing') { $bindings += @{ type = 'Microsoft.ContainerRegistry/registries'; name = (& $get 'registryName'); api = '2023-07-01'; group = (& $get 'registryResourceGroup') } }
        if ($Stage -eq 'application' -or -not (& $get 'createIdentities')) {
            foreach ($name in @('webIdentityName','workerIdentityName')) { $bindings += @{ type = 'Microsoft.ManagedIdentity/userAssignedIdentities'; name = (& $get $name); api = '2023-01-31'; group = $group } }
        }
        if ($Stage -eq 'compute') {
            $bindings += @{ type = 'Microsoft.Storage/storageAccounts'; name = (& $get 'storageAccountName'); api = '2023-05-01'; group = (& $get 'storageResourceGroup'); kind = 'StorageV2' }
            if ((& $get 'grantInference')) { $bindings += @{ type = 'Microsoft.CognitiveServices/accounts'; name = (& $get 'foundryAccountName'); api = '2025-06-01'; group = (& $get 'foundryResourceGroup') } }
            if ((& $get 'monitoringEnabled')) { $bindings += @{ type = 'Microsoft.OperationalInsights/workspaces'; name = (& $get 'workspaceName'); api = '2023-09-01'; group = (& $get 'workspaceResourceGroup') } }
        }
    }
    foreach ($binding in $bindings) {
        $bindingGroup = if ($binding.group -and -not $binding.group.StartsWith('[')) { $binding.group } else { $group }
        $resource = Get-PiiResource $subscription $bindingGroup $binding.type $binding.name $binding.api
        if ($resource.type -ine $binding.type -or $resource.name -cne $binding.name -or $resource.location -cne $Manifest.location) { throw 'Existing resource name/type/region mismatch.' }
        if ($binding.ContainsKey('kind') -and $resource.kind -cne $binding.kind) { throw 'Existing account kind mismatch; no type conversion allowed.' }
        if ($binding.type -eq 'Microsoft.Storage/storageAccounts') {
            if ($resource.properties.allowBlobPublicAccess -ne $false -or $resource.properties.supportsHttpsTrafficOnly -ne $true) { throw 'Existing storage must forbid public blobs and require HTTPS.' }
            if ($resource.properties.allowSharedKeyAccess -ne $false -and -not ((& $get 'developmentSharedKeyOptIn') -and $Manifest.environmentName -eq 'dev')) { throw 'Existing shared-key storage needs explicit development opt-in.' }
            foreach ($container in @((& $get 'documentsName'), (& $get 'keysName'))) {
                $child = Get-PiiResource $subscription $bindingGroup 'Microsoft.Storage/storageAccounts' "$($binding.name)/blobServices/default/containers/$container" '2023-05-01'
                if ($child.properties.publicAccess -cne 'None') { throw 'Existing containers must be private.' }
            }
        }
        if ($binding.type -eq 'Microsoft.CognitiveServices/accounts' -and $Stage -eq 'foundry') {
            if ((& $get 'createProject') -and -not $resource.properties.allowProjectManagement) { throw 'Existing AIServices does not enable project management; binding cannot mutate it.' }
            if ((& $get 'bindProject')) { $null = Get-PiiResource $subscription $bindingGroup 'Microsoft.CognitiveServices/accounts' "$($binding.name)/projects/$(& $get 'projectName')" '2025-06-01' }
            $endpoint = if ($resource.kind -eq 'OpenAI') { $resource.properties.endpoint } else { $resource.properties.endpoints.'OpenAI Language Model Instance' }
            if ($endpoint -notmatch '^https://[a-zA-Z0-9-]+\.openai\.azure\.com/?$') { throw 'SDK-compatible OpenAI root endpoint not available.' }
        }
        if ($binding.type -eq 'Microsoft.Sql/servers') { $null = Get-PiiResource $subscription $bindingGroup 'Microsoft.Sql/servers' "$($binding.name)/databases/$(& $get 'databaseName')" '2023-08-01' }
        if ($binding.type -eq 'Microsoft.App/managedEnvironments' -and @($resource.properties.workloadProfiles | Where-Object { $_.workloadProfileType -eq 'Consumption' }).Count -eq 0) { throw 'Existing environment must support Consumption.' }
        if ($binding.type -eq 'Microsoft.ContainerRegistry/registries') {
            if ($resource.properties.adminUserEnabled) { throw 'Existing registry must disable admin credentials.' }
            if ($Stage -eq 'application' -and (& $get 'deployWorkloads')) {
                $actualLoginServer = $resource.properties.loginServer
                foreach ($name in @('webImage','workerImage')) {
                    if ((& $get $name).Split('/')[0] -cne $actualLoginServer) { throw 'Both workload image hosts must match the actual registry loginServer.' }
                }
            }
        }
    }
}

function Invoke-PiiStage {
    [CmdletBinding()]
    param(
        [ValidateSet('foundation','foundry','storage','data','monitoring','compute','application')][string]$Stage,
        [Parameter(Mandatory)][string]$ManifestPath,
        [ValidateSet('WhatIf','Validate','Apply')][string]$Action,
        [switch]$Offline, [switch]$AuthorizeApply, [switch]$AcknowledgeCost,
        [string]$SecureParametersPath = '', [string]$WhatIfReceiptPath = ''
    )
    $manifest = Read-PiiManifest $ManifestPath
    $SubscriptionId = $manifest.subscriptionId
    $ManifestPath = $manifest.selection.manifestPath
    $section = $manifest.stages.$Stage
    $deploymentGroup = $manifest.resourceGroupName
    if ($section.PSObject.Properties.Name -contains 'resourceGroupName') {
        if ($Stage -eq 'foundation' -and $section.resourceGroupName -cne $deploymentGroup) { throw 'Foundation must use manifest resource group.' }
        $deploymentGroup = $section.resourceGroupName
    }
    if ($section.PSObject.Properties.Name -contains 'existingResourceId') {
        $types = @{ foundry = 'Microsoft.CognitiveServices/accounts'; storage = 'Microsoft.Storage/storageAccounts'; data = 'Microsoft.Sql/servers' }
        if (-not $types.ContainsKey($Stage) -or $section.existingResourceId -notmatch '^/subscriptions/([^/]+)/resourceGroups/([^/]+)/providers/([^/]+/[^/]+)/([^/]+)$') { throw 'existingResourceId must identify a supported account or SQL server, not an arbitrary child.' }
        if ($Matches[1] -cne $SubscriptionId -or $Matches[3] -ine $types[$Stage]) { throw 'Existing resource ID subscription/type mismatch.' }
        $deploymentGroup = $Matches[2]
        $bindingName = $Matches[4]
        $nameParameter = if ($Stage -eq 'data') { 'serverName' } else { 'accountName' }
        if ($section.parameters.mode -cne 'existing' -or $section.parameters.$nameParameter -cne $bindingName) { throw 'Existing ID must match explicit existing mode/name.' }
        if ($section.PSObject.Properties.Name -contains 'resourceGroupName' -and $section.resourceGroupName -cne $deploymentGroup) { throw 'Existing ID resource-group mismatch.' }
    }
    if (-not $section.enabled) { throw "Stage $Stage is disabled in manifest." }
    if ($Offline -and $Action -ne 'Validate') { throw 'Offline supports Validate only.' }
    if ($Action -eq 'Apply' -and (-not $AuthorizeApply -or -not $AcknowledgeCost)) { throw 'Apply requires -AuthorizeApply and -AcknowledgeCost.' }
    if ($Action -eq 'Apply' -and (ConvertTo-Json -InputObject $manifest -Depth 100) -match '"REPLACE-') { throw 'Replace manifest placeholders with verified values before Apply.' }
    $root = Split-Path $PSScriptRoot -Parent
    $templatePath = Join-Path $root "stages/$Stage.bicep"
    $compiledPath = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.json')
    try {
        $null = Invoke-PiiAz -Arguments @('bicep','build','--file',$templatePath,'--outfile',$compiledPath)
        $template = Get-Content $compiledPath -Raw | ConvertFrom-Json
        $parameters = @{}
        foreach ($property in $section.parameters.PSObject.Properties) { $parameters[$property.Name] = @{ value = $property.Value } }
        $globals = @{ location = $manifest.location; tags = $manifest.tags; prefix = $manifest.prefix; environmentName = $manifest.environmentName; resourceGroupName = $manifest.resourceGroupName }
        foreach ($key in $globals.Keys) {
            if ($template.parameters.PSObject.Properties.Name -contains $key -and -not $parameters.ContainsKey($key)) { $parameters[$key] = @{ value = $globals[$key] } }
        }
        foreach ($key in $parameters.Keys) {
            if ($template.parameters.PSObject.Properties.Name -notcontains $key) { throw "Unknown $Stage parameter: $key." }
            if ($template.parameters.$key.type -like 'secure*') { throw "Secure parameter $key must use a separate protected parameter file." }
        }
        if ($SecureParametersPath) {
            $secure = Get-Content -LiteralPath $SecureParametersPath -Raw | ConvertFrom-Json
            foreach ($property in $secure.parameters.PSObject.Properties) {
                if ($template.parameters.PSObject.Properties.Name -notcontains $property.Name -or $template.parameters.($property.Name).type -notlike 'secure*') { throw 'Protected parameter files may only supply declared secure parameters.' }
                if ($property.Value.PSObject.Properties.Name -notcontains 'value') { throw 'Use protected local secure values; Key Vault parameter references are not handled by this wrapper.' }
                $parameters[$property.Name] = @{ value = $property.Value.value }
            }
        }
        foreach ($property in $template.parameters.PSObject.Properties) {
            if (-not $parameters.ContainsKey($property.Name) -and $property.Value.PSObject.Properties.Name -notcontains 'defaultValue' -and $property.Value.type -notlike 'secure*') { throw "Missing $Stage parameter: $($property.Name)." }
        }
        Test-PiiParameters $parameters $template $manifest $Stage
        if ($Offline) { return "Passed local compilation and parameter contract: $Stage. Azure deployment validation NotRun." }
        $secureText = if ($SecureParametersPath) { Get-Content -LiteralPath $SecureParametersPath -Raw } else { '' }
        $effectiveConfiguration = [pscustomobject]@{ subscriptionId = $SubscriptionId; resourceGroupName = $manifest.resourceGroupName; location = $manifest.location; prefix = $manifest.prefix; environmentName = $manifest.environmentName; shared = $manifest.shared; selection = $manifest.selection }
        $fingerprint = Get-PiiHash ((Get-Content -LiteralPath $ManifestPath -Raw) + (ConvertTo-Json -InputObject (Convert-PiiCanonicalValue $manifest) -Depth 100 -Compress) + (Get-Content -LiteralPath $compiledPath -Raw) + $secureText + $Stage)
        if (-not $WhatIfReceiptPath) { $WhatIfReceiptPath = Join-Path $HOME "pii-private/receipts/$($manifest.prefix)-$($manifest.environmentName)-$Stage.json" }
        if ($Action -eq 'Apply') {
            if (-not (Test-Path -LiteralPath $WhatIfReceiptPath)) { throw 'Apply requires a previously reviewed WhatIf receipt for this exact stage.' }
            $receipt = Get-Content -LiteralPath $WhatIfReceiptPath -Raw | ConvertFrom-Json
            if ($receipt.stage -cne $Stage -or $receipt.subscriptionId -cne $SubscriptionId -or $receipt.fingerprint -cne $fingerprint) { throw 'Reviewed stage inputs changed; rerun and review WhatIf.' }
        }
        $scopedManifest = $manifest | ConvertTo-Json -Depth 100 | ConvertFrom-Json
        $scopedManifest.resourceGroupName = $deploymentGroup
        Test-PiiPreflight $scopedManifest $Stage $parameters $template
        $parameterPath = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.parameters.json')
        try {
            $publicParameters = @{}
            foreach ($key in $parameters.Keys) { if ($template.parameters.$key.type -notlike 'secure*') { $publicParameters[$key] = $parameters[$key] } }
            @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion = '1.0.0.0'; parameters = $publicParameters } | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $parameterPath -Encoding UTF8
            $scope = if ($Stage -eq 'foundation') { 'sub' } else { 'group' }
            $base = @('deployment',$scope)
            $common = @('--subscription',$SubscriptionId,'--name',"$($manifest.prefix)-$($manifest.environmentName)-$Stage",'--template-file',$compiledPath,'--parameters',"@$parameterPath",'--only-show-errors')
            if ($scope -eq 'sub') { $common += @('--location',$manifest.location) } else { $common += @('--resource-group',$deploymentGroup,'--mode','Incremental') }
            if ($SecureParametersPath) { $common += @('--parameters',"@$((Resolve-Path -LiteralPath $SecureParametersPath).Path)") }
            $operation = @{ WhatIf = 'what-if'; Validate = 'validate'; Apply = 'create' }[$Action]
            if ($Action -in @('WhatIf','Apply')) {
                $preview = Invoke-PiiAz -Arguments ($base + @('what-if') + $common + @('--no-pretty-print','--result-format','FullResourcePayloads','--output','json'))
                $changes = @(ConvertFrom-PiiWhatIfOutput -Output $preview | Sort-Object resourceId)
                $changeHash = Get-PiiHash (ConvertTo-Json -InputObject (Convert-PiiCanonicalValue $changes) -Depth 100 -Compress)
                foreach ($change in $changes) {
                    if ($change.changeType -eq 'Delete') { throw 'Deletion is not authorized by these incremental wrappers.' }
                    Write-Output "$($change.changeType): $($change.resourceId)"
                }
                if ($Action -eq 'Apply' -and $receipt.changeHash -cne $changeHash) { throw 'Stage ARM preview drifted; rerun and review WhatIf.' }
                if ($Action -eq 'WhatIf') {
                    $directory = Split-Path $WhatIfReceiptPath -Parent
                    if ($directory -and -not (Test-Path -LiteralPath $directory)) { $null = New-Item -ItemType Directory -Path $directory -Force }
                    @{ stage = $Stage; subscriptionId = $SubscriptionId; fingerprint = $fingerprint; changeHash = $changeHash; effectiveConfiguration = $effectiveConfiguration } | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $WhatIfReceiptPath -Encoding UTF8
                    Write-Output "Review selected inputs and displayed changes; receipt: $WhatIfReceiptPath. No resource payloads archived."
                }
            }
            if ($Action -ne 'WhatIf') { $null = Invoke-PiiAz -Arguments ($base + @($operation) + $common + @('--output','none')) }
            return "Completed $Action for $Stage; outputs intentionally suppressed."
        } finally { if (Test-Path $parameterPath) { Remove-Item -LiteralPath $parameterPath -Force } }
    } finally { if (Test-Path $compiledPath) { Remove-Item -LiteralPath $compiledPath -Force } }
}
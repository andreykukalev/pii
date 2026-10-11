[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$infra = Split-Path $PSScriptRoot -Parent
. (Join-Path $infra 'scripts/Deployment.Common.ps1')
$realAz = ${function:Invoke-PiiAz}
$script:calls = @()
$script:resourceResponse = $null
$script:resourceResponses = @{}
$script:inventory = @()
$script:groupLocation = 'northeurope'
$script:changes = @()
$script:whatIfNote = $false
$script:cache = @{}
$script:checks = 0
$savedManifestSelector = $env:PII_MANIFEST_PATH
$savedSubscriptionSelector = $env:PII_SUBSCRIPTION_ID
function Invoke-PiiAz {
    param([string[]]$Arguments)
    if ($Arguments[0] -eq 'bicep') {
        $source = $Arguments[[array]::IndexOf($Arguments, '--file') + 1]
        $output = $Arguments[[array]::IndexOf($Arguments, '--outfile') + 1]
        if (-not $script:cache.ContainsKey($source)) {
            $null = & $realAz -Arguments $Arguments
            $script:cache[$source] = Get-Content $output -Raw
        } else { [IO.File]::WriteAllText($output, $script:cache[$source]) }
        return ''
    }
    $script:calls += ,$Arguments
    if ($Arguments -notcontains '--subscription') { throw 'Mock caught implicit subscription.' }
    if ($Arguments[0] -eq 'group' -and $Arguments[1] -eq 'exists') { return 'true' }
    if ($Arguments[0] -eq 'group') { return (@{ location = $script:groupLocation; tags = @{ application = 'pii'; environment = 'dev' } } | ConvertTo-Json) }
    if ($Arguments[0] -eq 'resource' -and $Arguments[1] -eq 'list') { return (ConvertTo-Json -InputObject @($script:inventory) -Depth 20) }
    if ($Arguments[0] -eq 'resource') {
        $resourceId = $Arguments[[array]::IndexOf($Arguments, '--ids') + 1]
        if ($script:resourceResponses.ContainsKey($resourceId)) { return ($script:resourceResponses[$resourceId] | ConvertTo-Json -Depth 30) }
        return ($script:resourceResponse | ConvertTo-Json -Depth 30)
    }
    if ($Arguments[2] -eq 'what-if') {
        if ($Arguments -notcontains '--no-pretty-print') { throw 'WhatIf must disable pretty printing to return JSON.' }
        $json = @{ changes = $script:changes } | ConvertTo-Json -Depth 20
        if ($script:whatIfNote) { return "Note: Preview output is formatted as JSON.`r`n$json" }
        return $json
    }
    return '{}'
}
function Assert-Wrapper([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Wrapper contract failed: $Description" }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Check, [string]$Description, [string]$ExpectedMessage = '') {
    $rejected = $false
    try { $null = & $Check } catch { $rejected = -not $ExpectedMessage -or $_.Exception.Message.Contains($ExpectedMessage) }
    Assert-Wrapper $rejected $Description
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pii-wrappers-' + [guid]::NewGuid().ToString('N') + '.json')
$receiptPath = $temporary + '.receipt.json'
$securePath = $temporary + '.secure.json'
$manifestPath = Join-Path $PSScriptRoot 'fixtures/manifest.json'
$subscription = '11111111-1111-1111-1111-111111111111'
function Write-TestManifest($Manifest) { $Manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding UTF8 }
try {
    foreach ($file in Get-ChildItem $infra -Filter '*.ps1' -Recurse) {
        $tokens = $null
        $errors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        Assert-Wrapper ($errors.Count -eq 0) "syntax $($file.Name)"
    }
    foreach ($stage in @('foundation','foundry','storage','data','monitoring','compute','application')) {
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $manifest.stages.$stage.enabled = $true
        Write-TestManifest $manifest
        $result = Invoke-PiiStage -Stage $stage -ManifestPath $temporary -Action Validate -Offline
        Assert-Wrapper ($result -match 'Passed local') "$stage offline contract"
    }
    Assert-Wrapper ($script:calls.Count -eq 0) 'offline never performs cloud calls'
    $central = Read-PiiManifest $manifestPath
    $central.schemaVersion = 2
    foreach ($stage in $central.stages.PSObject.Properties) { $stage.Value.enabled = $true }
    $sharedNames = @('accountName','projectName','serverName','databaseName','tenantId','allowedObjectId','webClientId','managedEnvironmentName','registryName','registryResourceGroup','webIdentityName','workerIdentityName','storageAccountName','foundryAccountName','storageResourceGroup','foundryResourceGroup','workspaceName','workspaceResourceGroup','insightsName','documentsName','keysName','sqlServerFqdn','blobServiceUri','keyRingUri')
    foreach ($stage in $central.stages.PSObject.Properties) {
        foreach ($name in $sharedNames) { $stage.Value.parameters.PSObject.Properties.Remove($name) }
    }
    Write-TestManifest $central
    $env:PII_MANIFEST_PATH = 'not-the-selected-manifest.json'
    $env:PII_SUBSCRIPTION_ID = '55555555-5555-5555-5555-555555555555'
    $manifest = $temporary
    foreach ($stageName in @('foundation','foundry','storage','data','monitoring','compute','application','all')) {
        $wrapperPath = Join-Path $infra "scripts/deploy-$stageName.ps1"
        $result = & $wrapperPath -ManifestPath $temporary -Action Validate -Offline
        Assert-Wrapper (($result -join '') -match 'Passed local') "$stageName uses same explicit manifest despite environment mismatch"
        $command = Get-Command $wrapperPath
        Assert-Wrapper (@($command.Parameters['ManifestPath'].Attributes | Where-Object { $_ -is [Management.Automation.ParameterAttribute] -and $_.Mandatory }).Count -gt 0) "$stageName mandatory file selector"
        Assert-Rejected { & $wrapperPath -SubscriptionId $subscription -ManifestPath $temporary -Action Validate -Offline } "$stageName rejects removed SubscriptionId parameter" 'cannot be found that matches parameter name'
    }
    Assert-Wrapper ($manifest -eq $temporary) 'wrappers preserve caller manifest path variable'
    Assert-Wrapper ($script:calls.Count -eq 0) 'central wrappers never perform cloud calls offline'
    $env:PII_MANIFEST_PATH = $savedManifestSelector
    $env:PII_SUBSCRIPTION_ID = $savedSubscriptionSelector
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.application.enabled = $true
    $manifest.stages.application.parameters.deployWorkloads = $true
    $digest = 'a' * 64
    $registryHost = 'acrpiidevreplace.azurecr.io'
    $manifest.stages.application.parameters | Add-Member -NotePropertyName webImage -NotePropertyValue "$registryHost/pii/web@sha256:$digest"
    $manifest.stages.application.parameters | Add-Member -NotePropertyName workerImage -NotePropertyValue "$registryHost/pii/worker@sha256:$digest"
    @{ parameters = @{ webClientSecret = @{ value = 'test-only-not-a-real-credential' } } } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $securePath -Encoding UTF8
    foreach ($hostImage in @('webImage','workerImage')) {
        $originalImage = $manifest.stages.application.parameters.$hostImage
        $manifest.stages.application.parameters.$hostImage = "otherregistry.azurecr.io/pii/image@sha256:$digest"
        Write-TestManifest $manifest
        Assert-Rejected { Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline -SecureParametersPath $securePath } "$hostImage rejects another registry offline" 'selected registry'
        $manifest.stages.application.parameters.$hostImage = $originalImage
    }
    Write-TestManifest $manifest
    $null = Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline -SecureParametersPath $securePath
    Assert-Wrapper ($script:calls.Count -eq 0) 'matching digest hosts accepted without cloud calls'
    $appTemplate = $script:cache[(Join-Path $infra 'stages/application.bicep')] | ConvertFrom-Json
    $appParameters = @{}
    foreach ($property in $manifest.stages.application.parameters.PSObject.Properties) { $appParameters[$property.Name] = @{ value = $property.Value } }
    foreach ($bindingType in @('Microsoft.App/managedEnvironments','Microsoft.ContainerRegistry/registries','Microsoft.ManagedIdentity/userAssignedIdentities')) {
        $bindingNames = switch ($bindingType) {
            'Microsoft.App/managedEnvironments' { @('pii-dev-environment') }
            'Microsoft.ContainerRegistry/registries' { @('acrpiidevreplace') }
            default { @('pii-dev-web-mi','pii-dev-worker-mi') }
        }
        foreach ($bindingName in $bindingNames) {
            $script:resourceResponses["/subscriptions/$subscription/resourceGroups/pii-dev-rg/providers/$bindingType/$bindingName"] = @{ type = $bindingType; name = $bindingName; location = 'northeurope'; properties = @{ workloadProfiles = @(@{ workloadProfileType = 'Consumption' }); adminUserEnabled = $false; loginServer = $registryHost } }
        }
    }
    Test-PiiPreflight $manifest application $appParameters $appTemplate
    Assert-Wrapper $true 'matching actual registry loginServer accepted through mock'
    foreach ($hostImage in @('webImage','workerImage')) {
        $originalImage = $appParameters[$hostImage].value
        $appParameters[$hostImage].value = "otherregistry.azurecr.io/pii/image@sha256:$digest"
        Assert-Rejected { Test-PiiPreflight $manifest application $appParameters $appTemplate } "$hostImage checked against actual loginServer independently" 'actual registry loginServer'
        $appParameters[$hostImage].value = $originalImage
    }
    $registryId = "/subscriptions/$subscription/resourceGroups/pii-dev-rg/providers/Microsoft.ContainerRegistry/registries/acrpiidevreplace"
    $script:resourceResponses[$registryId].properties.loginServer = 'actualdifferent.azurecr.io'
    Assert-Rejected { Test-PiiPreflight $manifest application $appParameters $appTemplate } 'cloud registry metadata mismatch rejected despite offline host match' 'actual registry loginServer'
    $script:resourceResponses = @{}
    $script:calls = @()
    $manifest.stages.application.parameters.deployWorkloads = $false
    foreach ($uriName in @('blobServiceUri','keyRingUri')) {
        $originalUri = $manifest.stages.application.parameters.$uriName
        $invalidUris = if ($uriName -eq 'blobServiceUri') {
            @('https://stpiidevreplace.blob.core.windows.net/documents', ($originalUri + '?sig=test'), ($originalUri + '#fragment'), $originalUri.Replace('https:', 'http:'))
        } else {
            @('https://stpiidevreplace.blob.core.windows.net/', 'https://stpiidevreplace.blob.core.windows.net/keys/keyring.json', ($originalUri + '?sig=test'), ($originalUri + '#fragment'), $originalUri.Replace('https:', 'http:'))
        }
        foreach ($invalidUri in $invalidUris) {
            $manifest.stages.application.parameters.$uriName = $invalidUri
            Write-TestManifest $manifest
            $expectedMessage = if ($uriName -eq 'blobServiceUri') { 'HTTPS account root' } else { 'HTTPS XML blob path' }
            Assert-Rejected { Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline } "$uriName rejects invalid path/scheme/query/fragment: $invalidUri" $expectedMessage
        }
        $manifest.stages.application.parameters.$uriName = $originalUri
    }
    $manifest.stages.application.parameters.blobServiceUri = 'https://stpiidevreplace.blob.core.windows.net'
    Write-TestManifest $manifest
    $null = Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline
    Assert-Wrapper ($script:calls.Count -eq 0) 'Blob root without trailing slash and XML key blob accepted offline'
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action Apply -AuthorizeApply -AcknowledgeCost -WhatIfReceiptPath ($receiptPath + '.missing') } 'apply requires previously reviewed stage receipt'
    Assert-Wrapper ($script:calls.Count -eq 0) 'missing receipt rejected before cloud preflight'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.application.enabled = $true
    Write-TestManifest $manifest
    @{ parameters = @{ webClientSecret = @{ value = 'test-only-not-a-real-credential' } } } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $securePath -Encoding UTF8
    $result = Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline -SecureParametersPath $securePath
    Assert-Wrapper ($result -match 'Passed local' -and $result -notmatch 'test-only-not-a-real-credential') 'protected parameters accepted and never returned'
    @{ parameters = @{ deployWorkloads = @{ value = $true } } } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $securePath -Encoding UTF8
    Assert-Rejected { Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline -SecureParametersPath $securePath } 'protected files cannot override nonsecure activation'
    $manifest.stages.application.parameters | Add-Member -NotePropertyName webClientSecret -NotePropertyValue 'test-only-not-a-real-credential'
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline } 'secret values forbidden in ordinary manifest'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.storage.parameters.accountName = 'Invalid_Name'
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage storage -ManifestPath $temporary -Action Validate -Offline } 'invalid storage resource name'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.foundry.parameters.mode = 'existing'
    $manifest.stages.foundry.parameters.createProject = $false
    $manifest.stages.foundry | Add-Member -NotePropertyName existingResourceId -NotePropertyValue '/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/pii-dev-rg/providers/Microsoft.CognitiveServices/accounts/pii-dev-replace-ai'
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage foundry -ManifestPath $temporary -Action Validate -Offline } 'explicit existing ID wrong subscription'
    $manifest.stages.foundry.existingResourceId = "/subscriptions/$subscription/resourceGroups/pii-dev-rg/providers/Microsoft.CognitiveServices/accounts/wrong-name"
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage foundry -ManifestPath $temporary -Action Validate -Offline } 'existing ID wrong name'
    $manifest.stages.foundry.existingResourceId = "/subscriptions/$subscription/resourceGroups/pii-ai-rg/providers/Microsoft.CognitiveServices/accounts/pii-dev-replace-ai"
    Write-TestManifest $manifest
    $null = Invoke-PiiStage -Stage foundry -ManifestPath $temporary -Action Validate -Offline
    Assert-Wrapper $true 'existing ID same-subscription cross-RG supported'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.application.enabled = $true
    $manifest.stages.application.parameters | Add-Member -NotePropertyName liveAuthorized -NotePropertyValue $true
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline } 'live cannot bypass model rights/prices'
    $manifest.stages.application.parameters.liveAuthorized = $false
    $manifest.stages.application.parameters | Add-Member -NotePropertyName secCompanyFactsMaximumBytes -NotePropertyValue 2097152
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline } 'large SEC response needs separate approval'
    $manifest.stages.application.parameters.secCompanyFactsMaximumBytes = 1048576
    $manifest.stages.application.parameters | Add-Member -NotePropertyName twelveDataRightsAuthorized -NotePropertyValue $true
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline } 'provider cannot bypass retention/credit approval'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.PSObject.Properties.Remove('subscriptionId')
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $temporary -Action Validate -Offline } 'missing JSON subscription fails' 'Manifest subscriptionId'
    Assert-Rejected { Invoke-PiiStage -Stage foundation -SubscriptionId $subscription -ManifestPath $manifestPath -Action Validate -Offline } 'stage rejects removed SubscriptionId parameter' 'cannot be found that matches parameter name'
    Assert-Wrapper (@((Get-Command Invoke-PiiStage).Parameters['ManifestPath'].Attributes | Where-Object { $_ -is [Management.Automation.ParameterAttribute] -and $_.Mandatory }).Count -gt 0) 'stage mandatory selector'
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath '' -Action Validate -Offline } 'empty selected path fails'
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action Apply } 'apply without explicit authority'
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action WhatIf -Offline } 'offline cannot disguise cloud WhatIf'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.foundry.parameters | Add-Member -NotePropertyName deploymentSku -NotePropertyValue ProvisionedManaged
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage foundry -ManifestPath $temporary -Action Validate -Offline } 'PTU rejected'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.storage.parameters | Add-Member -NotePropertyName developmentSharedKeyOptIn -NotePropertyValue $true
    $manifest.environmentName = 'production'
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage storage -ManifestPath $temporary -Action Validate -Offline } 'production shared key rejected'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.data.parameters.computeModel = 'Basic'
    $manifest.stages.data.parameters | Add-Member -NotePropertyName maximumStorageGb -NotePropertyValue 3
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage data -ManifestPath $temporary -Action Validate -Offline } 'Basic storage capacity rejected'
    $manifest.stages.data.parameters.maximumStorageGb = 2
    Write-TestManifest $manifest
    $null = Invoke-PiiStage -Stage data -ManifestPath $temporary -Action Validate -Offline
    Assert-Wrapper $true 'Basic 5-DTU 2-GiB contract accepted'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.foundry.parameters.mode = 'existing'
    $manifest.stages.foundry.parameters.createProject = $false
    $manifest.stages.foundry.parameters | Add-Member -NotePropertyName existingAccountKind -NotePropertyValue OpenAI
    Write-TestManifest $manifest
    $null = Invoke-PiiStage -Stage foundry -ManifestPath $temporary -Action Validate -Offline
    Assert-Wrapper $true 'existing OpenAI binds without conversion'
    $manifest.stages.foundry.parameters.createProject = $true
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage foundry -ManifestPath $temporary -Action Validate -Offline } 'OpenAI project conversion rejected'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.application.enabled = $true
    $manifest.stages.application.parameters | Add-Member -NotePropertyName modelEndpoint -NotePropertyValue 'https://example.services.ai.azure.com/api/projects/test'
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage application -ManifestPath $temporary -Action Validate -Offline } 'project SDK endpoint rejected'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.stages.compute.enabled = $true
    $manifest.stages.application.enabled = $true
    Write-TestManifest $manifest
    $allSource = Get-Content (Join-Path $infra 'scripts/deploy-all.ps1') -Raw
    Assert-Wrapper ($allSource.Contains("@('foundation','foundry','storage','data','monitoring')") -and $allSource -notmatch 'Stage compute|Stage application') 'all excludes compute/application even if enabled'
    $script:groupLocation = 'westeurope'
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action Validate } 'mock RG region mismatch rejected'
    $script:groupLocation = 'northeurope'
    $script:inventory = @(@{ type = 'Microsoft.CognitiveServices/accounts'; name = 'pii-dev-replace-ai'; location = 'northeurope'; kind = 'OpenAI' })
    Assert-Rejected { Invoke-PiiStage -Stage foundry -ManifestPath $manifestPath -Action Validate } 'repeat create cannot convert OpenAI to AIServices'
    $script:inventory = @()
    $script:changes = @()
    $script:whatIfNote = $true
    $parsedChanges = @(ConvertFrom-PiiWhatIfOutput -Output "Note: Azure CLI informational message.`r`n{`r`n  `"changes`": []`r`n}")
    Assert-Wrapper ($parsedChanges.Count -eq 0) 'WhatIf JSON accepts Azure CLI Note lines'
    $escape = [char]27
    $decoratedPreview = [char]0xFEFF + "${escape}[33mNote: Azure CLI informational message.${escape}[0m`r`n{`r`n  `"changes`": []`r`n}"
    $parsedChanges = @(ConvertFrom-PiiWhatIfOutput -Output $decoratedPreview)
    Assert-Wrapper ($parsedChanges.Count -eq 0) 'WhatIf JSON accepts BOM and ANSI-decorated Note lines'
    $parsedChanges = @(ConvertFrom-PiiWhatIfOutput -Output "Preparing preview...`r`nNote: JSON follows.`r`n{`"changes`":[]}`r`nPreview complete.")
    Assert-Wrapper ($parsedChanges.Count -eq 0) 'WhatIf JSON is extracted from console notices'
    Assert-Rejected { ConvertFrom-PiiWhatIfOutput -Output 'Unexpected diagnostic without JSON' } 'WhatIf JSON rejects missing document'
    $null = Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action WhatIf -WhatIfReceiptPath $receiptPath
    $script:whatIfNote = $false
    $script:changes = @(@{ changeType = 'Delete'; resourceId = '/mock/resource' })
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action Apply -AuthorizeApply -AcknowledgeCost -WhatIfReceiptPath $receiptPath } 'destructive WhatIf rejected'
    Assert-Wrapper (@($script:calls | Where-Object { $_[0] -eq 'deployment' -and $_[2] -eq 'create' }).Count -eq 0) 'no apply after rejected preview'
    $script:changes = @()
    $null = Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action WhatIf -WhatIfReceiptPath $receiptPath
    Assert-Wrapper ($script:calls[-1][2] -eq 'what-if' -and $script:calls[-1] -contains '--subscription') 'mock WhatIf explicit subscription'
    $script:changes = @(@{ changeType = 'Modify'; resourceId = '/mock/resource'; before = @{ properties = @{ value = 1 } }; after = @{ properties = @{ value = 2 } } })
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action Apply -AuthorizeApply -AcknowledgeCost -WhatIfReceiptPath $receiptPath } 'per-stage preview drift rejected'
    $null = Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action WhatIf -WhatIfReceiptPath $receiptPath
    $script:changes[0].after.properties.value = 3
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action Apply -AuthorizeApply -AcknowledgeCost -WhatIfReceiptPath $receiptPath } 'same resource/change type property drift rejected'
    $script:changes[0].after.properties.value = 2
    $null = Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action Apply -AuthorizeApply -AcknowledgeCost -WhatIfReceiptPath $receiptPath
    Assert-Wrapper ($script:calls[-1][2] -eq 'create') 'matching reviewed stage may apply through mock only'
    $env:PII_MANIFEST_PATH = 'not-the-selected-manifest.json'
    $env:PII_SUBSCRIPTION_ID = '55555555-5555-5555-5555-555555555555'
    $null = Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action Apply -AuthorizeApply -AcknowledgeCost -WhatIfReceiptPath $receiptPath
    Assert-Wrapper ($script:calls[-1][2] -eq 'create') 'external environment mismatch does not invalidate reviewed manifest'
    Assert-Wrapper (@($script:calls | Where-Object { $_[[array]::IndexOf($_, '--subscription') + 1] -cne $subscription }).Count -eq 0) 'every cloud call uses manifest subscription'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    Write-TestManifest $manifest
    $null = Invoke-PiiStage -Stage foundation -ManifestPath $temporary -Action WhatIf -WhatIfReceiptPath $receiptPath
    $manifest.subscriptionId = '22222222-2222-2222-2222-222222222222'
    Write-TestManifest $manifest
    $priorCallCount = $script:calls.Count
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $temporary -Action Apply -AuthorizeApply -AcknowledgeCost -WhatIfReceiptPath $receiptPath } 'same-file subscription change invalidates receipt' 'Reviewed stage inputs changed'
    Assert-Wrapper ($script:calls.Count -eq $priorCallCount) 'manifest subscription drift rejected before cloud preflight'
    $env:PII_MANIFEST_PATH = $savedManifestSelector
    $env:PII_SUBSCRIPTION_ID = $savedSubscriptionSelector
    $null = Invoke-PiiStage -Stage foundation -ManifestPath $manifestPath -Action WhatIf -WhatIfReceiptPath $receiptPath
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.tags.environment = 'changed'
    Write-TestManifest $manifest
    Assert-Rejected { Invoke-PiiStage -Stage foundation -ManifestPath $temporary -Action Apply -AuthorizeApply -AcknowledgeCost -WhatIfReceiptPath $receiptPath } 'changed manifest rejected before apply'
    $receiptText = Get-Content $receiptPath -Raw
    Assert-Wrapper ($receiptText -notmatch 'before|after|properties|secret|password') 'receipt contains no ARM resource or secret payload'
    $receipt = $receiptText | ConvertFrom-Json
    Assert-Wrapper ($receipt.effectiveConfiguration.shared.storage.accountName -eq 'stpiidevreplace' -and $receipt.effectiveConfiguration.selection.manifestPath -eq (Resolve-Path $manifestPath).Path -and @($receipt.effectiveConfiguration.selection.PSObject.Properties).Count -eq 1) 'receipt includes nonsecret shared configuration and only manifest path provenance'
    Write-Output "Passed $script:checks PowerShell syntax, offline and mocked deployment safety checks. Only real CLI calls were local bicep build."
} finally {
    $env:PII_MANIFEST_PATH = $savedManifestSelector
    $env:PII_SUBSCRIPTION_ID = $savedSubscriptionSelector
    if (Test-Path $temporary) { Remove-Item -LiteralPath $temporary -Force }
    if (Test-Path $receiptPath) { Remove-Item -LiteralPath $receiptPath -Force }
    if (Test-Path $securePath) { Remove-Item -LiteralPath $securePath -Force }
}
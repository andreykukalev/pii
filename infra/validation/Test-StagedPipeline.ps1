[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$pipeline = Get-Content (Join-Path $root 'azure-pipelines.yml') -Raw
$script:checks = 0
function Assert-Pipeline([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Pipeline contract failed: $Description" }
    $script:checks++
}
$stages = @('foundation','foundry','storage','data','monitoring','compute','application')
Assert-Pipeline (@(Get-ChildItem (Join-Path $root 'infra/modules') -File | Where-Object { $_.Name -notlike 'runtime-*-access.bicep' }).Count -eq 0) 'only current runtime access modules remain'
Assert-Pipeline (@(Get-ChildItem (Join-Path $root 'infra') -File -Filter '*.bicep').Count -eq 0) 'no root composition template remains'
Assert-Pipeline (@(Get-ChildItem (Join-Path $root 'infra/parameters') -Recurse -File | Where-Object { $_.Directory.Name -eq 'legacy' }).Count -eq 0) 'no flat deployment parameters remain'
Assert-Pipeline (@(Get-ChildItem (Join-Path $root 'infra/validation') -File | Where-Object { $_.Name -match '^Test-(IaC|ParameterGates|DeploymentParameters)\.ps1$' }).Count -eq 0) 'no obsolete combined validators remain'
Assert-Pipeline ($pipeline.Contains('cp -R infra/stages infra/modules infra/scripts infra/sql "$out/infra/"') -and -not $pipeline.Contains('cp -R infra "$out/infra"')) 'release artifacts exclude deployment configuration'
Assert-Pipeline ($pipeline -match 'name: deploy\s+type: boolean\s+default: false') 'deployment defaults off'
Assert-Pipeline ($pipeline -match 'name: deploymentStage\s+type: string\s+default: foundation\s+values: \[foundation, foundry, storage, data, monitoring, compute, application\]') 'one explicit allowlisted stage per run'
Assert-Pipeline ($pipeline -match "name: secureParametersSecureFile\s+type: string\s+default: ''") 'protected parameters optional'
Assert-Pipeline ($pipeline -notmatch 'main\.bicep|main\.json|parameters/legacy|DeploymentParametersSecureFile|name: deployWorkloads|deploy-all\.ps1|jq |az deployment |az acr |docker push') 'no combined deployment, automatic stage expansion, image push or configuration rewrite'
Assert-Pipeline ([regex]::Matches($pipeline, 'secureFile: \$\(DeploymentManifestSecureFile\)').Count -eq 2) 'same central secure-file manifest in both jobs'
Assert-Pipeline ([regex]::Matches($pipeline, 'azureSubscription: \$\(AzureServiceConnection\)').Count -eq 2) 'existing federated service connection in both jobs'
Assert-Pipeline ([regex]::Matches($pipeline, 'addSpnToEnvironment: false').Count -eq 2) 'no credential environment export'
Assert-Pipeline ($pipeline -notmatch 'PII_SUBSCRIPTION_ID|PII_MANIFEST_PATH|PiiSubscriptionId|SubscriptionId\s*=|az account set') 'no competing subscription/path selector or automatic account switch'
Assert-Pipeline ([regex]::Matches($pipeline, 'Read-PiiManifest \$manifestPath').Count -eq 2) 'both jobs resolve selected JSON manifest'
Assert-Pipeline ([regex]::Matches($pipeline, 'scriptType: pscore').Count -eq 2) 'Linux PowerShell Core deployment tasks'
Assert-Pipeline ($pipeline.Contains('environment: $(DeploymentEnvironment)')) 'environment approval boundary retained'
foreach ($gate in @("eq(variables['PiiDeploymentAuthority'], 'true')", "eq(variables['PiiAfterTaxForecastApproved'], 'true')", "ne(variables['Build.Reason'], 'PullRequest')", "eq(variables['Build.SourceBranch'], 'refs/heads/main')", '${{ eq(parameters.deploy, true) }}')) {
    Assert-Pipeline ([regex]::Matches($pipeline, [regex]::Escape($gate)).Count -eq 2) "preview/apply gate $gate"
}
foreach ($validator in @('ManifestResolution','ModularStages','DeploymentWrappers','LocalIntegrationTemplates','StagedPipeline','Documentation','SchemaBootstrap','TrackedSecrets')) {
    Assert-Pipeline ($pipeline.Contains("./infra/validation/Test-$validator.ps1")) "CI runs $validator"
}
$blocks = [regex]::Matches($pipeline, '(?m)^(\s*)inlineScript: \|\r?\n((?:\1  .*\r?\n|\s*\r?\n)+)')
Assert-Pipeline ($blocks.Count -eq 2) 'exactly preview and apply scripts'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pii-pipeline-' + [guid]::NewGuid().ToString('N'))
$originalSecurePath = $env:PII_SECURE_PARAMETERS_PATH
$null = New-Item -ItemType Directory -Path $temporary
try {
    $sourceManifest = Join-Path $temporary 'source-manifest.json'
    $sourceSecure = Join-Path $temporary 'source-secure.json'
    Get-Content (Join-Path $PSScriptRoot 'fixtures/manifest.json') -Raw | Set-Content $sourceManifest -Encoding UTF8
    $expectedConfiguration = Get-Content $sourceManifest -Raw | ConvertFrom-Json
    $script:contextMismatch = ''
    '{"parameters":{}}' | Set-Content $sourceSecure -Encoding UTF8
    function az {
        $global:LASTEXITCODE = 0
        if ($args[0] -eq 'account') {
            Assert-Pipeline ($args[1] -eq 'show' -and $args -contains '--subscription' -and $args[[array]::IndexOf($args, '--subscription') + 1] -ceq $expectedConfiguration.subscriptionId) 'WIF context read scoped exclusively to manifest subscription'
            $tenant = $expectedConfiguration.stages.data.parameters.tenantId
            $identifier = $expectedConfiguration.subscriptionId
            if ($script:contextMismatch -eq 'tenant') { $tenant = '99999999-9999-9999-9999-999999999999' }
            if ($script:contextMismatch -eq 'subscription') { $identifier = '22222222-2222-2222-2222-222222222222' }
            return (@{ id = $identifier; tenantId = $tenant } | ConvertTo-Json)
        }
    }
    foreach ($stage in $stages) {
        Assert-Pipeline (Test-Path (Join-Path $root "infra/scripts/deploy-$stage.ps1")) "route exists $stage"
        foreach ($withSecure in @($false,$true)) {
            $env:PII_SECURE_PARAMETERS_PATH = if ($withSecure) { $sourceSecure } else { '' }
            $script:previewManifestPath = $null
            $script:stageInvocations = 0
            $invokeStage = {
                param($ManifestPath, $Action, $WhatIfReceiptPath, $SecureParametersPath, [switch]$AuthorizeApply, [switch]$AcknowledgeCost)
                $script:stageInvocations++
                Assert-Pipeline ((Get-Content $ManifestPath -Raw) -ceq (Get-Content $sourceManifest -Raw)) 'manifest copied without modification'
                Assert-Pipeline (($withSecure -and $SecureParametersPath -ceq $sourceSecure) -or (-not $withSecure -and -not $SecureParametersPath)) 'optional protected file routed unchanged'
                Assert-Pipeline ($WhatIfReceiptPath.Replace('\','/') -ceq (Join-Path $temporary "change-review/$stage.json").Replace('\','/')) 'selected stage receipt routed'
                if ($Action -eq 'WhatIf') {
                    Assert-Pipeline (-not $AuthorizeApply -and -not $AcknowledgeCost) 'preview never authorizes apply'
                    $script:previewManifestPath = $ManifestPath
                    $null = New-Item -ItemType Directory -Path (Split-Path $WhatIfReceiptPath -Parent) -Force
                    '{}' | Set-Content $WhatIfReceiptPath -Encoding UTF8
                } else {
                    Assert-Pipeline ($Action -ceq 'Apply' -and $AuthorizeApply -and $AcknowledgeCost) 'approved apply passes both explicit switches'
                    Assert-Pipeline ($ManifestPath -ceq $script:previewManifestPath -and (Test-Path $WhatIfReceiptPath)) 'apply uses stable selector and existing receipt'
                }
            }
            Assert-Pipeline ($invokeStage -is [scriptblock]) 'mock stage invocation available'
            foreach ($block in $blocks) {
                $source = $block.Groups[2].Value.Replace('$(Pipeline.Workspace)', $temporary.Replace('\','/')).Replace('$(manifestFile.secureFilePath)', $sourceManifest.Replace('\','/')).Replace('${{ parameters.deploymentStage }}', $stage)
                $source = $source.Replace("$($temporary.Replace('\','/'))/release/infra/scripts/Deployment.Common.ps1", (Join-Path $root 'infra/scripts/Deployment.Common.ps1').Replace('\','/'))
                $route = "& '$($temporary.Replace('\','/'))/release/infra/scripts/deploy-$stage.ps1' @arguments"
                Assert-Pipeline ($source.Contains($route)) 'script invokes selected single wrapper'
                $source = $source.Replace($route, '& $invokeStage @arguments')
                $tokens = $null
                $errors = $null
                $null = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                Assert-Pipeline ($errors.Count -eq 0) 'deployment script parses'
                & ([scriptblock]::Create($source))
                Assert-Pipeline (-not (Test-Path (Join-Path $temporary 'pii-config'))) 'private manifest copy removed after action'
                foreach ($mismatch in @('tenant','subscription')) {
                    $script:contextMismatch = $mismatch
                    $priorInvocationCount = $script:stageInvocations
                    $rejected = $false
                    try { & ([scriptblock]::Create($source)) } catch { $rejected = $_.Exception.Message.Contains('WIF connection scope does not match') }
                    Assert-Pipeline $rejected "WIF $mismatch mismatch blocks $stage action"
                    Assert-Pipeline ($script:stageInvocations -eq $priorInvocationCount) 'scope rejection never invokes stage wrapper'
                    Assert-Pipeline (-not (Test-Path (Join-Path $temporary 'pii-config'))) 'private manifest copy removed after context rejection'
                }
                $script:contextMismatch = ''
            }
        }
    }
    Assert-Pipeline (@(Get-ChildItem (Join-Path $temporary 'change-review') -File).Count -eq 7) 'review contains receipts only'
    Write-Output "Passed $script:checks staged pipeline routing, safety and PowerShell execution checks. Azure and deployment wrappers mocked; hosted YAML execution NotRun."
} finally {
    $env:PII_SECURE_PARAMETERS_PATH = $originalSecurePath
    Remove-Item $temporary -Recurse -Force
}
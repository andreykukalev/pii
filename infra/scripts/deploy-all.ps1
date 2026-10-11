[CmdletBinding()]
param([Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][ValidateSet('WhatIf','Validate','Apply')][string]$Action,
    [switch]$Offline, [switch]$AuthorizeApply, [switch]$AcknowledgeCost)
. "$PSScriptRoot/Deployment.Common.ps1"
$deploymentConfiguration = Read-PiiManifest $ManifestPath
foreach ($stage in @('foundation','foundry','storage','data','monitoring')) {
    if ($deploymentConfiguration.stages.$stage.enabled) { Invoke-PiiStage -Stage $stage @PSBoundParameters }
}
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SubscriptionId, [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][ValidateSet('WhatIf','Validate','Apply')][string]$Action,
    [switch]$Offline, [switch]$AuthorizeApply, [switch]$AcknowledgeCost)
. "$PSScriptRoot/Deployment.Common.ps1"
$manifest = Read-PiiManifest $ManifestPath $SubscriptionId
foreach ($stage in @('foundation','foundry','storage','data','monitoring')) {
    if ($manifest.stages.$stage.enabled) { Invoke-PiiStage -Stage $stage @PSBoundParameters }
}
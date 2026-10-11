[CmdletBinding()]
param([Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][ValidateSet('WhatIf','Validate','Apply')][string]$Action,
    [switch]$Offline, [switch]$AuthorizeApply, [switch]$AcknowledgeCost, [string]$SecureParametersPath = '', [string]$WhatIfReceiptPath = '')
. "$PSScriptRoot/Deployment.Common.ps1"
Invoke-PiiStage -Stage foundry @PSBoundParameters
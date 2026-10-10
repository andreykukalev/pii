[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$files = @(& git -C $root ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate repository files for credential signature check.' }
$patterns = @(
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    'AccountKey=[A-Za-z0-9+/]{40,}={0,2}',
    'SharedAccessSignature=sv=[^\s"'']+&sig=',
    '(?:ghp_|github_pat_)[A-Za-z0-9_]{30,}',
    'AKIA[A-Z0-9]{16}'
)
$failed = @()
foreach ($file in $files) {
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    if ([IO.Path]::GetExtension($path) -notin @('.json', '.yml', '.yaml', '.bicep', '.bicepparam', '.cs', '.csproj', '.props', '.md', '.ps1', '.sql', '.sh', '.txt', '.config', '.env') -and $file -notmatch 'Dockerfile|dockerignore') { continue }
    $content = Get-Content -LiteralPath $path -Raw
    foreach ($pattern in $patterns) { if ($content -match $pattern) { $failed += $file; break } }
}
if ($failed.Count -gt 0) { throw "Credential signature detected in: $($failed -join ', '). Values suppressed." }
Write-Output "Credential signature check passed for $($files.Count) enumerated files. This limited signature check is not a full secret audit."
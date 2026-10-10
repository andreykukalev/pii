[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This isolated schema check requires Windows SQL LocalDB, never Azure SQL.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$databaseName = 'PiiIaCSchema_' + [guid]::NewGuid().ToString('N')
$master = New-Object System.Data.SqlClient.SqlConnection 'Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;Connect Timeout=30'
$database = $null
$created = $false
$script:checks = 0
function Assert-Schema([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Local schema contract failed: $Description" }
    $script:checks++
}
function Get-SqlScalar($Connection, [string]$Sql) {
    $command = $Connection.CreateCommand()
    try { $command.CommandText = $Sql; $command.CommandTimeout = 30; return $command.ExecuteScalar() }
    finally { $command.Dispose() }
}
function Invoke-SqlStatement($Connection, [string]$Sql) {
    $command = $Connection.CreateCommand()
    try { $command.CommandText = $Sql; $command.CommandTimeout = 30; $command.ExecuteNonQuery() | Out-Null }
    finally { $command.Dispose() }
}
try {
    $master.Open()
    Invoke-SqlStatement $master "CREATE DATABASE [$databaseName] COLLATE Latin1_General_100_BIN2;"
    $created = $true
    $database = New-Object System.Data.SqlClient.SqlConnection "Server=(localdb)\MSSQLLocalDB;Database=$databaseName;Integrated Security=true;Connect Timeout=30"
    $database.Open()
    $schema = Get-Content (Join-Path $root 'infra/sql/schema-bootstrap.sql') -Raw
    $schema = [regex]::Replace($schema, '(?m)^:ON ERROR EXIT\r?\n', '')
    $schema = $schema.Replace('$(DatabaseName)', $databaseName)
    Invoke-SqlStatement $database $schema
    Assert-Schema ((Get-SqlScalar $database 'SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0;') -eq 9) 'exact nine tables'
    Assert-Schema ((Get-SqlScalar $database 'SELECT COUNT(*) FROM dbo.WorkspaceHeads WHERE Id=1 AND CurrentRevision=0 AND LastRevision=0;') -eq 1) 'empty workspace head initialized'
    foreach ($index in @('IX_ImportPreviews_BaselineRevision_Hash', 'IX_AnalysisRuns_IdempotencyKey', 'IX_AnalysisRuns_Status_ClaimExpires_CreatedAt', 'IX_SourceCreditEntries_Vendor_UtcDate')) {
        Assert-Schema ((Get-SqlScalar $database "SELECT COUNT(*) FROM sys.indexes WHERE name=N'$index';") -eq 1) "required index $index"
    }
    foreach ($table in @('BudgetMonths', 'BudgetReservations')) {
        Assert-Schema ((Get-SqlScalar $database "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.$table') AND precision=19 AND scale=8;") -eq 1) "$table decimal19,8 reservation metadata"
    }
    foreach ($table in @('WorkspaceHeads', 'AnalysisRuns', 'BudgetMonths')) {
        Assert-Schema ((Get-SqlScalar $database "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.$table') AND name=N'RowVersion' AND system_type_id=189;") -eq 1) "$table SQL rowversion"
    }
    $secondRejected = $false
    try { Invoke-SqlStatement $database $schema }
    catch { $secondRejected = $_.Exception.ToString().Contains('Schema bootstrap requires an empty database') }
    Assert-Schema $secondRejected 'bootstrap refuses nonempty database instead of overwriting'
    $grants = Get-Content (Join-Path $root 'infra/sql/runtime-users.sql') -Raw
    $grantStatements = [regex]::Matches($grants, '(?m)^GRANT (?:SELECT, INSERT, UPDATE, DELETE|SELECT, INSERT, UPDATE|SELECT, INSERT) ON OBJECT::dbo\.\w+ TO \[pii_(?:web|worker)_runtime\];')
    Assert-Schema ($grantStatements.Count -eq 16) 'exact table-scoped runtime grants'
    Invoke-SqlStatement $database 'CREATE USER [pii_web_runtime] WITHOUT LOGIN; CREATE USER [pii_worker_runtime] WITHOUT LOGIN;'
    foreach ($statement in $grantStatements) { Invoke-SqlStatement $database $statement.Value }
    foreach ($principal in @('pii_web_runtime', 'pii_worker_runtime')) {
        Invoke-SqlStatement $database "EXECUTE AS USER = N'$principal';"
        try {
            foreach ($table in @('WorkspaceHeads', 'PortfolioRevisions', 'ImportPreviews', 'LedgerIdentities', 'AnalysisRuns', 'BudgetMonths', 'BudgetReservations')) {
                foreach ($permission in @('SELECT', 'INSERT', 'UPDATE', 'DELETE')) {
                    Assert-Schema ((Get-SqlScalar $database "SELECT HAS_PERMS_BY_NAME(N'dbo.$table', N'OBJECT', N'$permission');") -eq 1) "$principal $table $permission"
                }
            }
            Assert-Schema ((Get-SqlScalar $database "SELECT HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE');") -eq 0) "$principal cannot create tables"
            Assert-Schema ((Get-SqlScalar $database "SELECT HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'ALTER');") -eq 0) "$principal cannot alter schema"
            Assert-Schema ((Get-SqlScalar $database "SELECT IS_ROLEMEMBER(N'db_owner');") -eq 0) "$principal is not database owner"
            foreach ($table in @('SourceCreditDays', 'SourceCreditEntries')) {
                foreach ($permission in @('SELECT', 'INSERT', 'UPDATE', 'DELETE')) {
                    $expected = 0
                    if ($principal -eq 'pii_worker_runtime' -and ($permission -in @('SELECT', 'INSERT') -or ($table -eq 'SourceCreditDays' -and $permission -eq 'UPDATE'))) { $expected = 1 }
                    Assert-Schema ((Get-SqlScalar $database "SELECT HAS_PERMS_BY_NAME(N'dbo.$table', N'OBJECT', N'$permission');") -eq $expected) "$principal $table $permission is $expected"
                }
            }
            $expectedTables = 7
            if ($principal -eq 'pii_worker_runtime') { $expectedTables = 9 }
            Assert-Schema ((Get-SqlScalar $database "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0;") -eq $expectedTables) "$principal sees permitted precreated schema for runtime initialization"
        }
        finally { Invoke-SqlStatement $database 'REVERT;' }
    }
    Assert-Schema ((Get-SqlScalar $database "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.SourceCreditDays') AND name=N'UtcDate' AND system_type_id=40;") -eq 1) 'credit day is SQL date'
    Assert-Schema ((Get-SqlScalar $database "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.SourceCreditDays') AND name=N'ConsumedCredits' AND system_type_id=127;") -eq 1) 'consumption is native bigint credits'
    Assert-Schema ((Get-SqlScalar $database "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.SourceCreditEntries') AND name=N'ReservationId' AND system_type_id=36;") -eq 1) 'reservation identity is native GUID'
    Assert-Schema ((Get-SqlScalar $database "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.SourceCreditEntries') AND referenced_object_id=OBJECT_ID(N'dbo.SourceCreditDays') AND delete_referential_action=0;") -eq 1) 'credit-only no-action relationship, no portfolio cascade'
    $upgrade = Get-Content (Join-Path $root 'infra/sql/source-credit-upgrade.sql') -Raw
    $upgrade = [regex]::Replace($upgrade, '(?m)^:ON ERROR EXIT\r?\n', '').Replace('$(DatabaseName)', $databaseName)
    Invoke-SqlStatement $database 'INSERT dbo.BudgetMonths (Month, TotalUsd) VALUES (202610, 0.75); DROP TABLE dbo.SourceCreditEntries; DROP TABLE dbo.SourceCreditDays;'
    Invoke-SqlStatement $database $upgrade
    Invoke-SqlStatement $database "INSERT dbo.SourceCreditDays (Vendor, UtcDate, MaximumCredits, ConsumedCredits) VALUES (N'TwelveData', CONVERT(date,SYSUTCDATETIME()), 10, 5);"
    Invoke-SqlStatement $database $upgrade
    Assert-Schema ((Get-SqlScalar $database 'SELECT ConsumedCredits FROM dbo.SourceCreditDays;') -eq 5) 'repeated upgrade preserves daily consumption'
    Assert-Schema ((Get-SqlScalar $database 'SELECT TotalUsd FROM dbo.BudgetMonths;') -eq 0.75) 'additive upgrade preserves monthly USD budget'
    Invoke-SqlStatement $database "EXECUTE AS USER = N'pii_worker_runtime';"
    try { Assert-Schema ((Get-SqlScalar $database "SELECT HAS_PERMS_BY_NAME(N'dbo.SourceCreditEntries', N'OBJECT', N'INSERT');") -eq 1) 'upgrade grants worker entry insertion' }
    finally { Invoke-SqlStatement $database 'REVERT;' }
    Write-Output "Passed $script:checks actual isolated LocalDB schema/metadata/table-grant checks. Entra SID creation and Azure runtime access remain NotRun."
}
finally {
    if ($null -ne $database) { $database.Dispose() }
    [System.Data.SqlClient.SqlConnection]::ClearAllPools()
    if ($created -and $databaseName -cmatch '^PiiIaCSchema_[a-f0-9]{32}$') {
        Invoke-SqlStatement $master "ALTER DATABASE [$databaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$databaseName];"
    }
    $master.Dispose()
}
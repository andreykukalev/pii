:ON ERROR EXIT
SET XACT_ABORT ON;
IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb') OR DB_NAME() <> N'$(DatabaseName)'
    THROW 51000, 'Connect to the reviewed development database, not master.', 1;
IF TRY_CONVERT(uniqueidentifier, '$(DeveloperObjectId)') IS NULL
    OR '$(DeveloperObjectId)' = '00000000-0000-0000-0000-000000000000'
    OR '$(DeveloperPrincipalType)' NOT IN ('E', 'X')
    THROW 51000, 'Verified Entra User object ID (E) or Group object ID (X) is required.', 1;
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'pii_developer')
    THROW 51000, 'Review existing developer principal; no implicit rebind.', 1;
IF (SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name IN
    (N'WorkspaceHeads', N'PortfolioRevisions', N'ImportPreviews', N'LedgerIdentities', N'AnalysisRuns', N'BudgetMonths', N'BudgetReservations', N'SourceCreditDays', N'SourceCreditEntries')) <> 9
    THROW 51000, 'Apply the complete schema with a separate DDL administrator first.', 1;
BEGIN TRANSACTION;
DECLARE @objectId uniqueidentifier = CONVERT(uniqueidentifier, '$(DeveloperObjectId)');
DECLARE @sid nvarchar(34) = CONVERT(varchar(34), CONVERT(varbinary(16), @objectId), 1);
DECLARE @create nvarchar(256) = N'CREATE USER [pii_developer] WITH SID = ' + @sid + N', TYPE = $(DeveloperPrincipalType);';
EXEC (@create);
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.WorkspaceHeads TO [pii_developer];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.PortfolioRevisions TO [pii_developer];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ImportPreviews TO [pii_developer];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.LedgerIdentities TO [pii_developer];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.AnalysisRuns TO [pii_developer];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BudgetMonths TO [pii_developer];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BudgetReservations TO [pii_developer];
GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.SourceCreditDays TO [pii_developer];
GRANT SELECT, INSERT ON OBJECT::dbo.SourceCreditEntries TO [pii_developer];
COMMIT;
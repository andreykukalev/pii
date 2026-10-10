:ON ERROR EXIT
SET XACT_ABORT ON;
IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb') OR DB_NAME() <> N'$(DatabaseName)'
    THROW 51000, 'Connect to the reviewed Pii database, not master.', 1;
IF TRY_CONVERT(uniqueidentifier, '$(WebManagedIdentityClientId)') IS NULL OR TRY_CONVERT(uniqueidentifier, '$(WorkerManagedIdentityClientId)') IS NULL
    OR '$(WebManagedIdentityClientId)' = '$(WorkerManagedIdentityClientId)'
    OR '$(WebManagedIdentityClientId)' = '00000000-0000-0000-0000-000000000000'
    OR '$(WorkerManagedIdentityClientId)' = '00000000-0000-0000-0000-000000000000'
    THROW 51000, 'Two distinct verified managed identity client IDs are required, not object IDs.', 1;
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name IN (N'pii_web_runtime', N'pii_worker_runtime'))
    THROW 51000, 'Existing principals must be reviewed, not rebound implicitly.', 1;
IF (SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name IN
    (N'WorkspaceHeads', N'PortfolioRevisions', N'ImportPreviews', N'LedgerIdentities', N'AnalysisRuns', N'BudgetMonths', N'BudgetReservations', N'SourceCreditDays', N'SourceCreditEntries')) <> 9
    THROW 51000, 'Apply and review the complete schema first.', 1;
BEGIN TRANSACTION;
DECLARE @webClientId uniqueidentifier = CONVERT(uniqueidentifier, '$(WebManagedIdentityClientId)');
DECLARE @workerClientId uniqueidentifier = CONVERT(uniqueidentifier, '$(WorkerManagedIdentityClientId)');
DECLARE @webSid nvarchar(34) = CONVERT(varchar(34), CONVERT(varbinary(16), @webClientId), 1);
DECLARE @workerSid nvarchar(34) = CONVERT(varchar(34), CONVERT(varbinary(16), @workerClientId), 1);
DECLARE @webCreate nvarchar(256) = N'CREATE USER [pii_web_runtime] WITH SID = ' + @webSid + N', TYPE = E;';
DECLARE @workerCreate nvarchar(256) = N'CREATE USER [pii_worker_runtime] WITH SID = ' + @workerSid + N', TYPE = E;';
EXEC (@webCreate);
EXEC (@workerCreate);
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.WorkspaceHeads TO [pii_web_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.PortfolioRevisions TO [pii_web_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ImportPreviews TO [pii_web_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.LedgerIdentities TO [pii_web_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.AnalysisRuns TO [pii_web_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BudgetMonths TO [pii_web_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BudgetReservations TO [pii_web_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.WorkspaceHeads TO [pii_worker_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.PortfolioRevisions TO [pii_worker_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ImportPreviews TO [pii_worker_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.LedgerIdentities TO [pii_worker_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.AnalysisRuns TO [pii_worker_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BudgetMonths TO [pii_worker_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BudgetReservations TO [pii_worker_runtime];
GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.SourceCreditDays TO [pii_worker_runtime];
GRANT SELECT, INSERT ON OBJECT::dbo.SourceCreditEntries TO [pii_worker_runtime];
COMMIT;
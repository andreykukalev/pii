:ON ERROR EXIT
SET XACT_ABORT ON;
IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 51000, 'Connect directly to the intended Pii database.', 1;
IF DB_NAME() <> N'$(DatabaseName)'
    THROW 51000, 'DatabaseName does not match the connected database.', 1;
IF CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')) <> N'Latin1_General_100_BIN2'
    THROW 51000, 'Expected case-sensitive binary workspace collation.', 1;
IF EXISTS (SELECT 1 FROM sys.tables WHERE is_ms_shipped = 0)
    THROW 51000, 'Schema bootstrap requires an empty database; review migrations instead.', 1;
BEGIN TRANSACTION;
CREATE TABLE dbo.WorkspaceHeads (
    Id int NOT NULL CONSTRAINT PK_WorkspaceHeads PRIMARY KEY,
    CurrentRevision int NOT NULL,
    LastRevision int NOT NULL,
    RowVersion rowversion NOT NULL
);
CREATE TABLE dbo.PortfolioRevisions (
    Revision int NOT NULL CONSTRAINT PK_PortfolioRevisions PRIMARY KEY,
    Payload nvarchar(max) NOT NULL
);
CREATE TABLE dbo.ImportPreviews (
    Id nvarchar(128) NOT NULL CONSTRAINT PK_ImportPreviews PRIMARY KEY,
    BaselineRevision int NOT NULL,
    Hash nvarchar(128) NOT NULL,
    ConfirmedRevision int NULL,
    Payload nvarchar(max) NOT NULL
);
CREATE UNIQUE INDEX IX_ImportPreviews_BaselineRevision_Hash ON dbo.ImportPreviews (BaselineRevision, Hash);
CREATE TABLE dbo.LedgerIdentities (
    Revision int NOT NULL,
    TransactionId nvarchar(256) NOT NULL,
    CONSTRAINT PK_LedgerIdentities PRIMARY KEY (Revision, TransactionId)
);
CREATE TABLE dbo.AnalysisRuns (
    Id nvarchar(128) NOT NULL CONSTRAINT PK_AnalysisRuns PRIMARY KEY,
    IdempotencyKey nvarchar(256) NOT NULL,
    Status int NOT NULL,
    CreatedAt datetimeoffset NOT NULL,
    Deadline datetimeoffset NOT NULL,
    ClaimExpires datetimeoffset NULL,
    Owner nvarchar(128) NULL,
    Generation int NOT NULL,
    Version bigint NOT NULL,
    CancelRequested bit NOT NULL,
    Payload nvarchar(max) NOT NULL,
    RowVersion rowversion NOT NULL
);
CREATE UNIQUE INDEX IX_AnalysisRuns_IdempotencyKey ON dbo.AnalysisRuns (IdempotencyKey);
CREATE INDEX IX_AnalysisRuns_Status_ClaimExpires_CreatedAt ON dbo.AnalysisRuns (Status, ClaimExpires, CreatedAt);
CREATE TABLE dbo.BudgetMonths (
    Month int NOT NULL CONSTRAINT PK_BudgetMonths PRIMARY KEY,
    TotalUsd decimal(19,8) NOT NULL,
    RowVersion rowversion NOT NULL
);
CREATE TABLE dbo.BudgetReservations (
    Id nvarchar(256) NOT NULL CONSTRAINT PK_BudgetReservations PRIMARY KEY,
    Month int NOT NULL,
    Usd decimal(19,8) NOT NULL
);
CREATE TABLE dbo.SourceCreditDays (
    Vendor nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    UtcDate date NOT NULL,
    MaximumCredits int NOT NULL,
    ConsumedCredits bigint NOT NULL,
    CONSTRAINT PK_SourceCreditDays PRIMARY KEY (Vendor, UtcDate),
    CONSTRAINT CK_SourceCreditDays_Bounds CHECK (Vendor = N'TwelveData' AND MaximumCredits > 0 AND ConsumedCredits >= 0)
);
CREATE TABLE dbo.SourceCreditEntries (
    ReservationId uniqueidentifier NOT NULL CONSTRAINT PK_SourceCreditEntries PRIMARY KEY,
    Vendor nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    UtcDate date NOT NULL,
    Endpoint nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Credits int NOT NULL,
    RateCardVersion nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Outcome nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT CK_SourceCreditEntries_Bounds CHECK (Vendor = N'TwelveData' AND Credits > 0 AND Outcome = N'ChargedUncertain'),
    CONSTRAINT FK_SourceCreditEntries_SourceCreditDays_Vendor_UtcDate FOREIGN KEY (Vendor, UtcDate)
        REFERENCES dbo.SourceCreditDays (Vendor, UtcDate) ON DELETE NO ACTION
);
CREATE INDEX IX_SourceCreditEntries_Vendor_UtcDate ON dbo.SourceCreditEntries (Vendor, UtcDate);
INSERT dbo.WorkspaceHeads (Id, CurrentRevision, LastRevision) VALUES (1, 0, 0);
COMMIT;
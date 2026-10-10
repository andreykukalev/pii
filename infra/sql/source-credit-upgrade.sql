:ON ERROR EXIT
SET XACT_ABORT ON;
IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb') OR DB_NAME() <> N'$(DatabaseName)'
    OR OBJECT_ID(N'dbo.WorkspaceHeads', N'U') IS NULL
    THROW 51000, 'Connect directly to the reviewed existing Pii workspace database.', 1;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource = N'Pii.SourceCreditSchema', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 5000;
IF @lockResult < 0 THROW 51000, 'Source credit schema lock unavailable.', 1;
IF OBJECT_ID(N'dbo.SourceCreditDays', N'U') IS NULL AND OBJECT_ID(N'dbo.SourceCreditEntries', N'U') IS NULL
BEGIN
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
END
ELSE IF OBJECT_ID(N'dbo.SourceCreditDays', N'U') IS NULL OR OBJECT_ID(N'dbo.SourceCreditEntries', N'U') IS NULL
    THROW 51000, 'Partial source credit schema requires operator review.', 1;
IF DATABASE_PRINCIPAL_ID(N'pii_worker_runtime') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.SourceCreditDays TO [pii_worker_runtime];
    GRANT SELECT, INSERT ON OBJECT::dbo.SourceCreditEntries TO [pii_worker_runtime];
END;
COMMIT;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Pii.Infrastructure;

public enum SourceCreditAdmission { Reserved, AlreadyReserved, Exhausted }

public interface ISourceCreditStore
{
    Task<SourceCreditAdmission> ReserveAsync(Guid reservationId, string endpoint, int credits, int dailyMaximumCredits,
        string rateCardVersion, CancellationToken cancellation);
}

public sealed class SqlSourceCreditStore(IDbContextFactory<WorkspaceDbContext> factory) : ISourceCreditStore
{
    public static async Task AddSchemaAsync(IDbContextFactory<WorkspaceDbContext> factory, CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await database.Database.ExecuteSqlRawAsync("""
            SET XACT_ABORT ON;
            IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb') OR OBJECT_ID(N'dbo.WorkspaceHeads', N'U') IS NULL
                THROW 51000, 'An existing reviewed Pii workspace database is required.', 1;
            BEGIN TRANSACTION;
            DECLARE @lockResult int;
            EXEC @lockResult = sys.sp_getapplock @Resource = N'Pii.SourceCreditSchema', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 5000;
            IF @lockResult < 0 THROW 51000, 'Source credit schema lock unavailable.', 1;
            IF OBJECT_ID(N'dbo.SourceCreditDays', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SourceCreditEntries', N'U') IS NOT NULL
            BEGIN
                COMMIT;
                RETURN;
            END;
            IF OBJECT_ID(N'dbo.SourceCreditDays', N'U') IS NOT NULL OR OBJECT_ID(N'dbo.SourceCreditEntries', N'U') IS NOT NULL
                THROW 51000, 'Partial source credit schema requires operator review.', 1;
            """ + SchemaSql + "COMMIT;", cancellation);
    }

    private const string SchemaSql = """
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
        """;

    public async Task<SourceCreditAdmission> ReserveAsync(Guid reservationId, string endpoint, int credits, int dailyMaximumCredits,
        string rateCardVersion, CancellationToken cancellation)
    {
        if (reservationId == Guid.Empty || endpoint is not ("eod" or "quote" or "time_series" or "press_releases")
            || credits <= 0 || dailyMaximumCredits <= 0 || string.IsNullOrWhiteSpace(rateCardVersion)
            || rateCardVersion.Length > 128 || rateCardVersion.Any(char.IsControl))
            throw new ArgumentException("Explicit positive Twelve Data credits, daily quota, endpoint and rate-card version are required.");
        for (var attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                await using var database = await factory.CreateDbContextAsync(cancellation);
                var connection = (SqlConnection)database.Database.GetDbConnection();
                await database.Database.OpenConnectionAsync(cancellation);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = 5;
                command.CommandText = """
                    DECLARE @utcDate date = CONVERT(date, SYSUTCDATETIME());
                    DECLARE @existingCredits int, @existingEndpoint nvarchar(32), @existingRate nvarchar(128);
                    SELECT @existingCredits = Credits, @existingEndpoint = Endpoint, @existingRate = RateCardVersion
                    FROM dbo.SourceCreditEntries WITH (UPDLOCK, HOLDLOCK) WHERE ReservationId = @id;
                    IF @existingCredits IS NOT NULL
                    BEGIN
                        IF @existingCredits <> @credits OR @existingEndpoint <> @endpoint OR @existingRate <> @rate
                            THROW 50001, 'Source credit reservation identity conflict.', 1;
                        SELECT 1;
                        RETURN;
                    END;
                    DECLARE @consumed bigint;
                    SELECT @consumed = ConsumedCredits FROM dbo.SourceCreditDays WITH (UPDLOCK, HOLDLOCK)
                    WHERE Vendor = N'TwelveData' AND UtcDate = @utcDate;
                    IF @consumed IS NULL
                    BEGIN
                        INSERT dbo.SourceCreditDays (Vendor, UtcDate, MaximumCredits, ConsumedCredits)
                        VALUES (N'TwelveData', @utcDate, @maximum, 0);
                        SET @consumed = 0;
                    END
                    ELSE
                        UPDATE dbo.SourceCreditDays SET MaximumCredits = @maximum WHERE Vendor = N'TwelveData' AND UtcDate = @utcDate;
                    IF @consumed + @credits > @maximum
                    BEGIN
                        SELECT 2;
                        RETURN;
                    END;
                    UPDATE dbo.SourceCreditDays SET ConsumedCredits = ConsumedCredits + @credits
                    WHERE Vendor = N'TwelveData' AND UtcDate = @utcDate;
                    INSERT dbo.SourceCreditEntries (ReservationId, Vendor, UtcDate, Endpoint, Credits, RateCardVersion, Outcome)
                    VALUES (@id, N'TwelveData', @utcDate, @endpoint, @credits, @rate, N'ChargedUncertain');
                    SELECT 0;
                    """;
                command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = reservationId;
                command.Parameters.Add("@endpoint", SqlDbType.NVarChar, 32).Value = endpoint;
                command.Parameters.Add("@credits", SqlDbType.Int).Value = credits;
                command.Parameters.Add("@maximum", SqlDbType.Int).Value = dailyMaximumCredits;
                command.Parameters.Add("@rate", SqlDbType.NVarChar, 128).Value = rateCardVersion;
                var admission = (SourceCreditAdmission)(int)(await command.ExecuteScalarAsync(cancellation))!;
                await transaction.CommitAsync(cancellation);
                return admission;
            }
            catch (SqlException error) when (attempt < 2 && error.Number is 1205 or 2601 or 2627 or 3960)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), cancellation);
            }
        }
    }
}
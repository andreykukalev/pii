using Microsoft.EntityFrameworkCore;
using Pii.Application;

namespace Pii.Infrastructure;

public sealed class WorkspaceDbContext(DbContextOptions<WorkspaceDbContext> options) : DbContext(options)
{
    public DbSet<WorkspaceHead> Heads => Set<WorkspaceHead>();
    public DbSet<RevisionRow> Revisions => Set<RevisionRow>();
    public DbSet<PreviewRow> Previews => Set<PreviewRow>();
    public DbSet<LedgerIdentityRow> LedgerIdentities => Set<LedgerIdentityRow>();
    public DbSet<RunRow> Runs => Set<RunRow>();
    public DbSet<MonthRow> Months => Set<MonthRow>();
    public DbSet<ReservationRow> Reservations => Set<ReservationRow>();
    public DbSet<SourceCreditDayRow> SourceCreditDays => Set<SourceCreditDayRow>();
    public DbSet<SourceCreditEntryRow> SourceCreditEntries => Set<SourceCreditEntryRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.UseCollation("Latin1_General_100_BIN2");
        model.Entity<WorkspaceHead>().ToTable("WorkspaceHeads").HasKey(row => row.Id);
        model.Entity<WorkspaceHead>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<WorkspaceHead>().Property(row => row.RowVersion).IsRowVersion();
        model.Entity<RevisionRow>().ToTable("PortfolioRevisions").HasKey(row => row.Revision);
        model.Entity<RevisionRow>().Property(row => row.Revision).ValueGeneratedNever();
        model.Entity<PreviewRow>().ToTable("ImportPreviews").HasKey(row => row.Id);
        model.Entity<PreviewRow>().Property(row => row.Id).HasMaxLength(128);
        model.Entity<PreviewRow>().Property(row => row.Hash).HasMaxLength(128);
        model.Entity<PreviewRow>().HasIndex(row => new { row.BaselineRevision, row.Hash }).IsUnique();
        model.Entity<LedgerIdentityRow>().ToTable("LedgerIdentities").HasKey(row => new { row.Revision, row.TransactionId });
        model.Entity<LedgerIdentityRow>().Property(row => row.TransactionId).HasMaxLength(256);
        model.Entity<RunRow>().ToTable("AnalysisRuns").HasKey(row => row.Id);
        model.Entity<RunRow>().Property(row => row.Id).HasMaxLength(128);
        model.Entity<RunRow>().Property(row => row.IdempotencyKey).HasMaxLength(256);
        model.Entity<RunRow>().Property(row => row.Owner).HasMaxLength(128);
        model.Entity<RunRow>().Property(row => row.RowVersion).IsRowVersion();
        model.Entity<RunRow>().HasIndex(row => row.IdempotencyKey).IsUnique();
        model.Entity<RunRow>().HasIndex(row => new { row.Status, row.ClaimExpires, row.CreatedAt });
        model.Entity<MonthRow>().ToTable("BudgetMonths").HasKey(row => row.Month);
        model.Entity<MonthRow>().Property(row => row.Month).ValueGeneratedNever();
        model.Entity<MonthRow>().Property(row => row.TotalUsd).HasPrecision(19, 8);
        model.Entity<MonthRow>().Property(row => row.RowVersion).IsRowVersion();
        model.Entity<ReservationRow>().ToTable("BudgetReservations").HasKey(row => row.Id);
        model.Entity<ReservationRow>().Property(row => row.Id).HasMaxLength(256);
        model.Entity<ReservationRow>().Property(row => row.Usd).HasPrecision(19, 8);
        model.Entity<SourceCreditDayRow>().ToTable("SourceCreditDays", table =>
            table.HasCheckConstraint("CK_SourceCreditDays_Bounds", "[Vendor] = N'TwelveData' AND [MaximumCredits] > 0 AND [ConsumedCredits] >= 0"))
            .HasKey(row => new { row.Vendor, row.UtcDate });
        model.Entity<SourceCreditDayRow>().Property(row => row.Vendor).HasMaxLength(32);
        model.Entity<SourceCreditEntryRow>().ToTable("SourceCreditEntries", table =>
            table.HasCheckConstraint("CK_SourceCreditEntries_Bounds", "[Vendor] = N'TwelveData' AND [Credits] > 0 AND [Outcome] = N'ChargedUncertain'"))
            .HasKey(row => row.ReservationId);
        model.Entity<SourceCreditEntryRow>().Property(row => row.ReservationId).ValueGeneratedNever();
        model.Entity<SourceCreditEntryRow>().Property(row => row.Vendor).HasMaxLength(32);
        model.Entity<SourceCreditEntryRow>().Property(row => row.Endpoint).HasMaxLength(32);
        model.Entity<SourceCreditEntryRow>().Property(row => row.RateCardVersion).HasMaxLength(128);
        model.Entity<SourceCreditEntryRow>().Property(row => row.Outcome).HasMaxLength(32);
        model.Entity<SourceCreditEntryRow>().HasOne<SourceCreditDayRow>().WithMany()
            .HasForeignKey(row => new { row.Vendor, row.UtcDate }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class WorkspaceHead
{
    public int Id { get; set; } = 1;
    public int CurrentRevision { get; set; }
    public int LastRevision { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
public sealed class RevisionRow
{
    public int Revision { get; set; }
    public string Payload { get; set; } = "";
}
public sealed class PreviewRow
{
    public string Id { get; set; } = "";
    public int BaselineRevision { get; set; }
    public string Hash { get; set; } = "";
    public int? ConfirmedRevision { get; set; }
    public string Payload { get; set; } = "";
}
public sealed class LedgerIdentityRow
{
    public int Revision { get; set; }
    public string TransactionId { get; set; } = "";
}
public sealed class RunRow
{
    public string Id { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public RunStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public DateTimeOffset? ClaimExpires { get; set; }
    public string? Owner { get; set; }
    public int Generation { get; set; }
    public long Version { get; set; }
    public bool CancelRequested { get; set; }
    public string Payload { get; set; } = "";
    public byte[] RowVersion { get; set; } = [];
}
public sealed class MonthRow
{
    public int Month { get; set; }
    public decimal TotalUsd { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
public sealed class ReservationRow
{
    public string Id { get; set; } = "";
    public int Month { get; set; }
    public decimal Usd { get; set; }
}

public sealed class SourceCreditDayRow
{
    public string Vendor { get; set; } = "TwelveData";
    public DateOnly UtcDate { get; set; }
    public int MaximumCredits { get; set; }
    public long ConsumedCredits { get; set; }
}

public sealed class SourceCreditEntryRow
{
    public Guid ReservationId { get; set; }
    public string Vendor { get; set; } = "TwelveData";
    public DateOnly UtcDate { get; set; }
    public string Endpoint { get; set; } = "";
    public int Credits { get; set; }
    public string RateCardVersion { get; set; } = "";
    public string Outcome { get; set; } = "ChargedUncertain";
}
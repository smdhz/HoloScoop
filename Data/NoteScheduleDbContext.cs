using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Data;

public sealed class NoteScheduleDbContext(DbContextOptions<NoteScheduleDbContext> options)
    : DbContext(options)
{
    public DbSet<NoteScheduleRow> HololiveSchedule => Set<NoteScheduleRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var schedule = modelBuilder.Entity<NoteScheduleRow>();
        schedule.ToTable("HololiveSchedule");
        schedule.HasKey(item => item.Id);
        schedule.Property(item => item.Id)
            .HasDefaultValueSql("newsequentialid()")
            .ValueGeneratedOnAdd();
        schedule.Property(item => item.StartDt)
            .HasConversion(
                value => NormalizeUtc(value),
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .HasColumnType("datetime2");
        schedule.Property(item => item.MemberName).HasMaxLength(64);
        schedule.Property(item => item.StreamUrl).HasMaxLength(512);
        schedule.Property(item => item.StreamTitle).HasMaxLength(255);
        schedule.Property(item => item.StreamImage).HasMaxLength(512);
        schedule.Property(item => item.MemberName).UseCollation("SQL_Latin1_General_CP1_CI_AS");
        schedule.Property(item => item.StreamUrl).UseCollation("SQL_Latin1_General_CP1_CI_AS");
        schedule.Property(item => item.StreamTitle).UseCollation("SQL_Latin1_General_CP1_CI_AS");
        schedule.Property(item => item.StreamImage).UseCollation("SQL_Latin1_General_CP1_CI_AS");
        schedule.Property(item => item.Md5)
            .HasColumnType("char(32)")
            .UseCollation("SQL_Latin1_General_CP1_CI_AS")
            .IsFixedLength();
        schedule.Property(item => item.IsArchive).HasDefaultValue(false);
        schedule.Property(item => item.CreatedAt)
            .HasConversion(
                value => NormalizeUtc(value),
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .HasColumnType("datetime2")
            .HasDefaultValueSql("sysutcdatetime()")
            .ValueGeneratedOnAdd();
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}

public sealed class NoteScheduleRow
{
    public Guid Id { get; set; }
    public DateTime StartDt { get; set; }
    public required string MemberName { get; set; }
    public required string StreamUrl { get; set; }
    public required string StreamTitle { get; set; }
    public string? StreamImage { get; set; }
    public required string Md5 { get; set; }
    public bool IsArchive { get; set; }
    public DateTime CreatedAt { get; set; }
}

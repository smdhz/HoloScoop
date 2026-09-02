using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Services.Note;

public sealed class NoteScheduleDbContext(DbContextOptions<NoteScheduleDbContext> options)
    : DbContext(options)
{
    public DbSet<NoteScheduleRow> HololiveSchedule => Set<NoteScheduleRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var schedule = modelBuilder.Entity<NoteScheduleRow>();
        schedule.ToTable("HololiveSchedule", "dbo");
        schedule.HasKey(item => item.Id);
        schedule.Property(item => item.MemberName).IsRequired();
        schedule.Property(item => item.StreamUrl).IsRequired();
        schedule.Property(item => item.StreamTitle).IsRequired();
        schedule.Property(item => item.Md5).IsRequired();
    }
}

public sealed class NoteScheduleRow
{
    public Guid Id { get; set; }
    public DateTimeOffset StartDt { get; set; }
    public required string MemberName { get; set; }
    public required string StreamUrl { get; set; }
    public required string StreamTitle { get; set; }
    public string? StreamImage { get; set; }
    public required string Md5 { get; set; }
    public bool IsArchive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

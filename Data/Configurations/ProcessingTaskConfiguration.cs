using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HoloScoop.Data.Configurations;

public sealed class ProcessingTaskConfiguration : IEntityTypeConfiguration<MediaTask>
{
    public void Configure(EntityTypeBuilder<MediaTask> builder)
    {
        builder.ToTable("Tasks", "dbo", table =>
        {
            table.HasCheckConstraint("CK_Tasks_DownloadMode",
                "[DownloadMode] IS NULL OR [DownloadMode] IN ('VideoAndSubtitles', 'SubtitlesOnly', 'VideoOnly')");
            table.HasCheckConstraint("CK_Tasks_Status",
                "[Status] IN ('PendingSelection', 'Queued', 'Downloading', 'ParsingSubtitles', 'Diarizing', 'Indexing', 'Completed', 'Failed', 'Expired')");
            table.HasCheckConstraint("CK_Tasks_AttemptCount", "[AttemptCount] >= 0");
            table.HasCheckConstraint("CK_Tasks_SpeakerCount",
                "[SpeakerCount] IS NULL OR ([SpeakerCount] >= 1 AND [SpeakerCount] <= 20)");
        });
        builder.HasKey(x => x.Id).HasName("PK_Tasks");
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.RedisStream).HasMaxLength(256).IsRequired();
        builder.Property(x => x.RedisMessageId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DownloadMode)
            .HasConversion<string>()
            .HasColumnType("varchar(32)");
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasColumnType("varchar(32)")
            .IsRequired();
        builder.Property(x => x.AttemptCount).HasDefaultValue(0);
        builder.Property(x => x.LastError).HasColumnType("nvarchar(max)");
        builder.Property(x => x.SpeakerNamesJson).HasMaxLength(2000);
        builder.Property(x => x.ScheduledMemberName).HasMaxLength(256);
        builder.Property(x => x.CreatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.UpdatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();

        builder.HasOne(x => x.Stream)
            .WithMany(x => x.Tasks)
            .HasForeignKey(x => x.StreamId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_Tasks_Streams_StreamId");
        builder.HasIndex(x => new { x.RedisStream, x.RedisMessageId })
            .IsUnique()
            .HasDatabaseName("UX_Tasks_RedisStream_RedisMessageId");
        builder.HasIndex(x => new { x.Status, x.UpdatedAt })
            .HasDatabaseName("IX_Tasks_Status_UpdatedAt");
        builder.HasIndex(x => x.StreamId).HasDatabaseName("IX_Tasks_StreamId");
    }
}

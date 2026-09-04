using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HoloScoop.Data.Configurations;

public sealed class SubtitleSegmentConfiguration : IEntityTypeConfiguration<SubtitleSegment>
{
    public void Configure(EntityTypeBuilder<SubtitleSegment> builder)
    {
        builder.ToTable("SubtitleSegments", "dbo", table =>
        {
            table.HasCheckConstraint("CK_SubtitleSegments_Sequence", "[Sequence] >= 0");
            table.HasCheckConstraint("CK_SubtitleSegments_TimeRange", "[StartMs] >= 0 AND [EndMs] > [StartMs]");
            table.HasCheckConstraint("CK_SubtitleSegments_SpeakerNameScore",
                "[SpeakerNameScore] IS NULL OR ([SpeakerNameScore] >= -1 AND [SpeakerNameScore] <= 1)");
        });
        builder.HasKey(x => x.Id).HasName("PK_SubtitleSegments");
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Language).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ModelVersion).HasMaxLength(256);
        builder.Property(x => x.Text).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.SpeakerLabel).HasMaxLength(64);
        builder.Property(x => x.SpeakerName).HasMaxLength(256);
        builder.Property(x => x.SpeakerNameSource).HasColumnType("varchar(32)");
        builder.Property(x => x.CreatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasOne(x => x.Stream)
            .WithMany(x => x.SubtitleSegments)
            .HasForeignKey(x => x.StreamId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_SubtitleSegments_Streams_StreamId");
        builder.HasIndex(x => new { x.StreamId, x.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_SubtitleSegments_StreamId_Sequence");
        builder.HasIndex(x => new { x.StreamId, x.StartMs })
            .HasDatabaseName("IX_SubtitleSegments_StreamId_StartMs");
        builder.HasIndex(x => new { x.StreamId, x.SpeakerName })
            .HasDatabaseName("IX_SubtitleSegments_StreamId_SpeakerName");
    }
}

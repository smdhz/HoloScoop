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
            table.HasCheckConstraint("CK_SubtitleSegments_TimeRange", "[StartMs] >= 0 AND [EndMs] >= [StartMs]");
        });
        builder.HasKey(x => x.Id).HasName("PK_SubtitleSegments");
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Language).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Source).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Text).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.CreatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasOne(x => x.Stream)
            .WithMany(x => x.SubtitleSegments)
            .HasForeignKey(x => x.StreamId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_SubtitleSegments_Streams_StreamId");
        builder.HasIndex(x => new { x.StreamId, x.Language, x.Source, x.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_SubtitleSegments_Stream_Language_Source_Sequence");
        builder.HasIndex(x => new { x.StreamId, x.StartMs })
            .HasDatabaseName("IX_SubtitleSegments_StreamId_StartMs");
    }
}

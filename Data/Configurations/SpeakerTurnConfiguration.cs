using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HoloScoop.Data.Configurations;

public sealed class SpeakerTurnConfiguration : IEntityTypeConfiguration<SpeakerTurn>
{
    public void Configure(EntityTypeBuilder<SpeakerTurn> builder)
    {
        builder.ToTable("SpeakerTurns", "dbo", table =>
        {
            table.HasCheckConstraint(
                "CK_SpeakerTurns_TimeRange",
                "[StartMs] >= 0 AND [EndMs] > [StartMs]");
            table.HasCheckConstraint(
                "CK_SpeakerTurns_SpeakerNameScore",
                "[SpeakerNameScore] IS NULL OR ([SpeakerNameScore] >= -1 AND [SpeakerNameScore] <= 1)");
            table.HasCheckConstraint(
                "CK_SpeakerTurns_SuggestedSpeakerScore",
                "[SuggestedSpeakerScore] IS NULL OR ([SuggestedSpeakerScore] >= -1 AND [SuggestedSpeakerScore] <= 1)");
        });
        builder.HasKey(x => x.Id).HasName("PK_SpeakerTurns");
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.SpeakerLabel).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SpeakerName).HasMaxLength(256);
        builder.Property(x => x.SpeakerNameSource).HasColumnType("varchar(32)");
        builder.Property(x => x.SuggestedSpeakerName).HasMaxLength(256);
        builder.Property(x => x.CreatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasOne(x => x.Stream)
            .WithMany(x => x.SpeakerTurns)
            .HasForeignKey(x => x.StreamId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_SpeakerTurns_Streams_StreamId");
        builder.HasIndex(x => new { x.StreamId, x.StartMs })
            .HasDatabaseName("IX_SpeakerTurns_StreamId_StartMs");
        builder.HasIndex(x => new { x.StreamId, x.SpeakerLabel })
            .HasDatabaseName("IX_SpeakerTurns_StreamId_SpeakerLabel");
    }
}

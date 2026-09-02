using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HoloScoop.Data.Configurations;

public sealed class SpeakerClusterEmbeddingConfiguration
    : IEntityTypeConfiguration<SpeakerClusterEmbedding>
{
    public void Configure(EntityTypeBuilder<SpeakerClusterEmbedding> builder)
    {
        builder.ToTable("SpeakerClusterEmbeddings", "dbo", table =>
            table.HasCheckConstraint("CK_SpeakerClusterEmbeddings_Dimension", "[Dimension] > 0"));
        builder.HasKey(x => x.Id).HasName("PK_SpeakerClusterEmbeddings");
        builder.Property(x => x.SpeakerLabel).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Embedding).HasColumnType("varbinary(max)").IsRequired();
        builder.Property(x => x.CreatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasOne(x => x.Stream)
            .WithMany()
            .HasForeignKey(x => x.StreamId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_SpeakerClusterEmbeddings_Streams_StreamId");
        builder.HasIndex(x => new { x.StreamId, x.SpeakerLabel })
            .IsUnique()
            .HasDatabaseName("UX_SpeakerClusterEmbeddings_StreamId_SpeakerLabel");
    }
}

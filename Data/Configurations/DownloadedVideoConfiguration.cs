using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HoloScoop.Data.Configurations;

public sealed class DownloadedVideoConfiguration : IEntityTypeConfiguration<DownloadedVideo>
{
    public void Configure(EntityTypeBuilder<DownloadedVideo> builder)
    {
        builder.ToTable("DownloadedVideos", "dbo");
        builder.HasKey(x => x.Id).HasName("PK_DownloadedVideos");
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.DownloadedAt).HasPrecision(3);

        builder.HasOne(x => x.Stream)
            .WithMany(x => x.DownloadedVideos)
            .HasForeignKey(x => x.StreamId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_DownloadedVideos_Streams_StreamId");
        builder.HasIndex(x => x.StreamId)
            .IsUnique()
            .HasDatabaseName("UX_DownloadedVideos_StreamId");
        builder.HasIndex(x => x.DownloadedAt)
            .HasDatabaseName("IX_DownloadedVideos_DownloadedAt");
    }
}

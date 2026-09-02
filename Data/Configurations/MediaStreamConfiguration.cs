using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MediaStream = HoloScoop.Data.Entities.Stream;

namespace HoloScoop.Data.Configurations;

public sealed class MediaStreamConfiguration : IEntityTypeConfiguration<MediaStream>
{
    public void Configure(EntityTypeBuilder<MediaStream> builder)
    {
        builder.ToTable("Streams", "dbo", table =>
            table.HasCheckConstraint("CK_Streams_DurationMs", "[DurationMs] IS NULL OR [DurationMs] >= 0"));
        builder.HasKey(x => x.Id).HasName("PK_Streams");
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Platform).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ExternalId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ChannelId).HasMaxLength(128);
        builder.Property(x => x.ChannelName).HasMaxLength(256);
        builder.Property(x => x.Title).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Description).HasColumnType("nvarchar(max)");
        builder.Property(x => x.SourceUrl).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ThumbnailUrl).HasMaxLength(2048);
        builder.Property(x => x.ScheduledAt).HasPrecision(3);
        builder.Property(x => x.StartedAt).HasPrecision(3);
        builder.Property(x => x.EndedAt).HasPrecision(3);
        builder.Property(x => x.CreatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.UpdatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(x => new { x.Platform, x.ExternalId })
            .IsUnique()
            .HasDatabaseName("UX_Streams_Platform_ExternalId");
    }
}

using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HoloScoop.Data.Configurations;

public sealed class VoiceProfileConfiguration : IEntityTypeConfiguration<VoiceProfile>
{
    public void Configure(EntityTypeBuilder<VoiceProfile> builder)
    {
        builder.ToTable("VoiceProfiles", "dbo", table =>
        {
            table.HasCheckConstraint("CK_VoiceProfiles_Dimension", "[Dimension] > 0");
            table.HasCheckConstraint("CK_VoiceProfiles_SampleCount", "[SampleCount] > 0");
        });
        builder.HasKey(x => x.Id).HasName("PK_VoiceProfiles");
        builder.Property(x => x.MemberName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Embedding).HasColumnType("varbinary(max)").IsRequired();
        builder.Property(x => x.CreatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.Property(x => x.UpdatedAt).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
        builder.HasIndex(x => x.MemberName).IsUnique().HasDatabaseName("UX_VoiceProfiles_MemberName");
    }
}

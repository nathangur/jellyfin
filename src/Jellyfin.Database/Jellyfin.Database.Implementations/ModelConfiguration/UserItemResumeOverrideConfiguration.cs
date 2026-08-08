using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// FluentAPI configuration for the UserItemResumeOverride entity.
/// </summary>
public class UserItemResumeOverrideConfiguration : IEntityTypeConfiguration<UserItemResumeOverride>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<UserItemResumeOverride> builder)
    {
        builder.HasKey(e => new { e.UserId, e.ItemId });

        // Covers the cascade delete performed when an item is removed from the library.
        builder.HasIndex(e => e.ItemId);

        builder.HasOne(e => e.User).WithMany().OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Item).WithMany().OnDelete(DeleteBehavior.Cascade);
    }
}

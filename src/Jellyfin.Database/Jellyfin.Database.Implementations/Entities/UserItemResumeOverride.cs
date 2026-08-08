using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// Records a user's decision to remove a title from the resume or next up list.
/// </summary>
/// <remarks>
/// A row holding an item's own id hides that item from the resume list; a row holding a series id
/// hides the series from next up. The override applies while <see cref="OverriddenAt"/> is at or
/// after the most recent play of anything it covers, so playing the title again lets it lapse
/// without any stored state having to be cleared.
/// </remarks>
public class UserItemResumeOverride
{
    /// <summary>
    /// Gets or sets the id of the user the override belongs to.
    /// </summary>
    /// <value>The user id.</value>
    public required Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the user the override belongs to.
    /// </summary>
    /// <value>The user.</value>
    public required User? User { get; set; }

    /// <summary>
    /// Gets or sets the id of the overridden title.
    /// </summary>
    /// <value>The item itself for resume overrides, the series for next up overrides.</value>
    public required Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the overridden item.
    /// </summary>
    /// <value>The item.</value>
    public required BaseItemEntity? Item { get; set; }

    /// <summary>
    /// Gets or sets the date the override was recorded.
    /// </summary>
    /// <value>The date the override was recorded, in UTC.</value>
    public required DateTime OverriddenAt { get; set; }
}

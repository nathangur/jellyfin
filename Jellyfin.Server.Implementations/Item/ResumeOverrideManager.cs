using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// Stores the per-user overrides that remove titles from the resume and next up lists.
/// </summary>
/// <remarks>
/// A single table serves both lists without needing to record which one an override belongs to,
/// because the two read paths look up different ids: the resume query matches an item's own id,
/// and the next up query matches a series id. Storing an episode's own id therefore only affects
/// resume, and storing a series id only affects next up.
/// </remarks>
public class ResumeOverrideManager : IResumeOverrideManager
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResumeOverrideManager"/> class.
    /// </summary>
    /// <param name="dbProvider">EFCore database factory.</param>
    public ResumeOverrideManager(IDbContextFactory<JellyfinDbContext> dbProvider)
    {
        _dbProvider = dbProvider;
    }

    /// <summary>
    /// Resolves the series an item belongs to, falling back to the item itself.
    /// </summary>
    /// <param name="item">The item the user acted on.</param>
    /// <returns>The series id, or the item's own id when it is not part of a series.</returns>
    public static Guid GetSeriesTarget(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item switch
        {
            Episode episode when !episode.SeriesId.IsEmpty() => episode.SeriesId,
            Season season when !season.SeriesId.IsEmpty() => season.SeriesId,
            _ => item.Id
        };
    }

    /// <inheritdoc />
    public Task HideFromResumeAsync(Guid userId, BaseItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        return UpsertAsync(userId, item.Id, cancellationToken);
    }

    /// <inheritdoc />
    public Task RestoreToResumeAsync(Guid userId, BaseItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        return DeleteAsync(userId, item.Id, cancellationToken);
    }

    /// <inheritdoc />
    public Task HideFromNextUpAsync(Guid userId, BaseItem item, CancellationToken cancellationToken = default)
        => UpsertAsync(userId, GetSeriesTarget(item), cancellationToken);

    /// <inheritdoc />
    public Task RestoreToNextUpAsync(Guid userId, BaseItem item, CancellationToken cancellationToken = default)
        => DeleteAsync(userId, GetSeriesTarget(item), cancellationToken);

    private async Task UpsertAsync(Guid userId, Guid targetId, CancellationToken cancellationToken)
    {
        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var existing = await db.UserItemResumeOverrides
                .FirstOrDefaultAsync(o => o.UserId.Equals(userId) && o.ItemId.Equals(targetId), cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                db.UserItemResumeOverrides.Add(new UserItemResumeOverride
                {
                    UserId = userId,
                    User = null,
                    ItemId = targetId,
                    Item = null,
                    OverriddenAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.OverriddenAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteAsync(Guid userId, Guid targetId, CancellationToken cancellationToken)
    {
        var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.UserItemResumeOverrides
                .Where(o => o.UserId.Equals(userId) && o.ItemId.Equals(targetId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

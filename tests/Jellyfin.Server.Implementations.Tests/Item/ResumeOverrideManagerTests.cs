using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Verifies which id <see cref="ResumeOverrideManager"/> records for each list. Resume overrides are
/// scoped to the item the user acted on; next up overrides are scoped to its series. That split is
/// what lets one table drive both lists without recording which list a row belongs to.
/// </summary>
public sealed class ResumeOverrideManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly ResumeOverrideManager _manager;

    public ResumeOverrideManagerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);

        _manager = new ResumeOverrideManager(factory.Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void GetSeriesTarget_Movie_ReturnsItemId()
    {
        var movie = new Movie { Id = Guid.NewGuid() };

        Assert.Equal(movie.Id, ResumeOverrideManager.GetSeriesTarget(movie));
    }

    [Fact]
    public void GetSeriesTarget_Episode_ReturnsSeriesId()
    {
        var seriesId = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };

        Assert.Equal(seriesId, ResumeOverrideManager.GetSeriesTarget(episode));
    }

    [Fact]
    public void GetSeriesTarget_Season_ReturnsSeriesId()
    {
        var seriesId = Guid.NewGuid();
        var season = new Season { Id = Guid.NewGuid(), SeriesId = seriesId };

        Assert.Equal(seriesId, ResumeOverrideManager.GetSeriesTarget(season));
    }

    // An orphaned episode has no series to fall back on, so it must not record an empty target.
    [Fact]
    public void GetSeriesTarget_EpisodeWithoutSeries_ReturnsItemId()
    {
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.Empty };

        Assert.Equal(episode.Id, ResumeOverrideManager.GetSeriesTarget(episode));
    }

    [Fact]
    public void GetSeriesTarget_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ResumeOverrideManager.GetSeriesTarget(null!));
    }

    // The distinction the whole design rests on: resume records the episode, next up the series.
    [Fact]
    public async Task HideFromResumeAsync_Episode_RecordsEpisodeIdNotSeries()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);

        await _manager.HideFromResumeAsync(
            seed.UserId,
            new Episode { Id = seed.EpisodeId, SeriesId = seed.SeriesId },
            token).ConfigureAwait(true);

        var only = Assert.Single(await ReadOverridesAsync().ConfigureAwait(true));
        Assert.Equal(seed.EpisodeId, only.ItemId);
    }

    [Fact]
    public async Task HideFromNextUpAsync_Episode_RecordsSeriesId()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);

        await _manager.HideFromNextUpAsync(
            seed.UserId,
            new Episode { Id = seed.EpisodeId, SeriesId = seed.SeriesId },
            token).ConfigureAwait(true);

        var only = Assert.Single(await ReadOverridesAsync().ConfigureAwait(true));
        Assert.Equal(seed.SeriesId, only.ItemId);
    }

    // Both lists can be suppressed at once, and they occupy separate rows.
    [Fact]
    public async Task HidingBothLists_RecordsTwoIndependentRows()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);
        var episode = new Episode { Id = seed.EpisodeId, SeriesId = seed.SeriesId };

        await _manager.HideFromResumeAsync(seed.UserId, episode, token).ConfigureAwait(true);
        await _manager.HideFromNextUpAsync(seed.UserId, episode, token).ConfigureAwait(true);

        var overrides = await ReadOverridesAsync().ConfigureAwait(true);
        Assert.Equal(2, overrides.Length);
        Assert.Contains(overrides, o => o.ItemId.Equals(seed.EpisodeId));
        Assert.Contains(overrides, o => o.ItemId.Equals(seed.SeriesId));
    }

    // Restoring one list must not restore the other.
    [Fact]
    public async Task RestoreToResumeAsync_LeavesNextUpOverrideInPlace()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);
        var episode = new Episode { Id = seed.EpisodeId, SeriesId = seed.SeriesId };

        await _manager.HideFromResumeAsync(seed.UserId, episode, token).ConfigureAwait(true);
        await _manager.HideFromNextUpAsync(seed.UserId, episode, token).ConfigureAwait(true);

        await _manager.RestoreToResumeAsync(seed.UserId, episode, token).ConfigureAwait(true);

        var only = Assert.Single(await ReadOverridesAsync().ConfigureAwait(true));
        Assert.Equal(seed.SeriesId, only.ItemId);
    }

    [Fact]
    public async Task HideFromResumeAsync_Movie_RecordsOverride()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);

        await _manager.HideFromResumeAsync(seed.UserId, new Movie { Id = seed.MovieId }, token).ConfigureAwait(true);

        var only = Assert.Single(await ReadOverridesAsync().ConfigureAwait(true));
        Assert.Equal(seed.UserId, only.UserId);
        Assert.Equal(seed.MovieId, only.ItemId);
    }

    // The composite key makes a repeat removal an update, not a duplicate.
    [Fact]
    public async Task HideFromResumeAsync_Twice_UpdatesInPlace()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);
        var movie = new Movie { Id = seed.MovieId };

        await _manager.HideFromResumeAsync(seed.UserId, movie, token).ConfigureAwait(true);
        var first = (await ReadOverridesAsync().ConfigureAwait(true)).Single().OverriddenAt;

        await _manager.HideFromResumeAsync(seed.UserId, movie, token).ConfigureAwait(true);

        var only = Assert.Single(await ReadOverridesAsync().ConfigureAwait(true));
        Assert.True(only.OverriddenAt >= first);
    }

    [Fact]
    public async Task RestoreToResumeAsync_ExistingOverride_DeletesIt()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);
        var movie = new Movie { Id = seed.MovieId };
        await _manager.HideFromResumeAsync(seed.UserId, movie, token).ConfigureAwait(true);

        await _manager.RestoreToResumeAsync(seed.UserId, movie, token).ConfigureAwait(true);

        Assert.Empty(await ReadOverridesAsync().ConfigureAwait(true));
    }

    // Clearing an override that was never recorded is a no-op, not an error.
    [Fact]
    public async Task RestoreToResumeAsync_NoOverride_DoesNothing()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);

        await _manager.RestoreToResumeAsync(seed.UserId, new Movie { Id = seed.MovieId }, token).ConfigureAwait(true);

        Assert.Empty(await ReadOverridesAsync().ConfigureAwait(true));
    }

    // Undo for next up is issued from whichever episode the user clicked, not necessarily the one
    // that was showing when they removed it.
    [Fact]
    public async Task RestoreToNextUpAsync_DifferentEpisodeOfSameSeries_ClearsOverride()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);

        await _manager.HideFromNextUpAsync(
            seed.UserId,
            new Episode { Id = seed.EpisodeId, SeriesId = seed.SeriesId },
            token).ConfigureAwait(true);

        await _manager.RestoreToNextUpAsync(
            seed.UserId,
            new Episode { Id = Guid.NewGuid(), SeriesId = seed.SeriesId },
            token).ConfigureAwait(true);

        Assert.Empty(await ReadOverridesAsync().ConfigureAwait(true));
    }

    [Fact]
    public async Task HideFromResumeAsync_OtherUser_IsIsolated()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = await SeedAsync().ConfigureAwait(true);

        Guid otherUserId;
        var seedCtx = CreateDbContext();
        await using (seedCtx.ConfigureAwait(false))
        {
            var other = new User("other", "auth-provider", "reset-provider");
            seedCtx.Users.Add(other);
            await seedCtx.SaveChangesAsync(token).ConfigureAwait(true);
            otherUserId = other.Id;
        }

        await _manager.HideFromResumeAsync(seed.UserId, new Movie { Id = seed.MovieId }, token).ConfigureAwait(true);

        var overrides = await ReadOverridesAsync().ConfigureAwait(true);
        Assert.Single(overrides, o => o.UserId.Equals(seed.UserId));
        Assert.DoesNotContain(overrides, o => o.UserId.Equals(otherUserId));
    }

    private async Task<UserItemResumeOverride[]> ReadOverridesAsync()
    {
        var ctx = CreateDbContext();
        await using (ctx.ConfigureAwait(false))
        {
            return await ctx.UserItemResumeOverrides
                .AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<(Guid UserId, Guid MovieId, Guid SeriesId, Guid EpisodeId)> SeedAsync()
    {
        var ctx = CreateDbContext();
        await using (ctx.ConfigureAwait(false))
        {
            var user = new User("test", "auth-provider", "reset-provider");
            ctx.Users.Add(user);

            var movie = NewItem("MediaBrowser.Controller.Entities.Movies.Movie");
            var series = NewItem("MediaBrowser.Controller.Entities.TV.Series");
            var episode = NewItem("MediaBrowser.Controller.Entities.TV.Episode");
            episode.SeriesId = series.Id;
            ctx.BaseItems.AddRange(movie, series, episode);

            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

            return (user.Id, movie.Id, series.Id, episode.Id);
        }
    }

    private static BaseItemEntity NewItem(string type)
        => new() { Id = Guid.NewGuid(), Type = type, Name = "Item" };

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Verifies that the resume and next up lists are suppressed independently: removing an item from
/// resume hides only that item, removing a series from next up hides only the series from next up,
/// and each override lapses once the user plays the relevant title again.
/// </summary>
public sealed class ResumeOverrideTests : IDisposable
{
    private static readonly DateTime _played = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _laterThanPlayed = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _earlierThanPlayed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly BaseItemRepository _repository;
    private readonly NextUpService _nextUpService;
    private readonly ItemTypeLookup _itemTypeLookup;
    private readonly Guid _libraryId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public ResumeOverrideTests()
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

        _itemTypeLookup = new ItemTypeLookup();

        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        _repository = new BaseItemRepository(
            factory.Object,
            new Mock<IServerApplicationHost>().Object,
            _itemTypeLookup,
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);

        _nextUpService = new NextUpService(factory.Object, _itemTypeLookup, _repository);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    // The regression guard: with no overrides the resume list must be untouched.
    [Fact]
    public void ResumeQuery_NoOverrides_ReturnsInProgressItems()
    {
        var movieId = Guid.NewGuid();
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var movie = AddMovie(ctx, movieId);
            AddUserData(ctx, movie, user, positionTicks: 1000, lastPlayedDate: _played);
            ctx.SaveChanges();
        }

        Assert.Equal([movieId], ResumeIds(userId));
    }

    [Fact]
    public void ResumeQuery_OverriddenMovie_IsHidden()
    {
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var movie = AddMovie(ctx, Guid.NewGuid());
            AddUserData(ctx, movie, user, positionTicks: 1000, lastPlayedDate: _played);
            AddOverride(ctx, user, movie.Id, _laterThanPlayed);
            ctx.SaveChanges();
        }

        Assert.Empty(ResumeIds(userId));
    }

    [Fact]
    public void ResumeQuery_OverriddenMoviePlayedSince_IsVisible()
    {
        var movieId = Guid.NewGuid();
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var movie = AddMovie(ctx, movieId);
            AddUserData(ctx, movie, user, positionTicks: 1000, lastPlayedDate: _laterThanPlayed);
            AddOverride(ctx, user, movie.Id, _earlierThanPlayed);
            ctx.SaveChanges();
        }

        Assert.Equal([movieId], ResumeIds(userId));
    }

    // A play landing exactly on the override timestamp must not lapse it.
    [Fact]
    public void ResumeQuery_PlayedExactlyAtOverrideTime_StaysHidden()
    {
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var movie = AddMovie(ctx, Guid.NewGuid());
            AddUserData(ctx, movie, user, positionTicks: 1000, lastPlayedDate: _played);
            AddOverride(ctx, user, movie.Id, _played);
            ctx.SaveChanges();
        }

        Assert.Empty(ResumeIds(userId));
    }

    [Fact]
    public void ResumeQuery_NullLastPlayedDate_StaysHidden()
    {
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var movie = AddMovie(ctx, Guid.NewGuid());
            AddUserData(ctx, movie, user, positionTicks: 1000, lastPlayedDate: null);
            AddOverride(ctx, user, movie.Id, _played);
            ctx.SaveChanges();
        }

        Assert.Empty(ResumeIds(userId));
    }

    [Fact]
    public void ResumeQuery_OtherUsersOverride_DoesNotHideItem()
    {
        var movieId = Guid.NewGuid();
        Guid subjectId;

        using (var ctx = CreateDbContext())
        {
            var subject = AddUser(ctx, "subject");
            var other = AddUser(ctx, "other");
            subjectId = subject.Id;

            var movie = AddMovie(ctx, movieId);
            AddUserData(ctx, movie, subject, positionTicks: 1000, lastPlayedDate: _played);
            AddUserData(ctx, movie, other, positionTicks: 1000, lastPlayedDate: _played);
            AddOverride(ctx, other, movie.Id, _laterThanPlayed);
            ctx.SaveChanges();
        }

        Assert.Equal([movieId], ResumeIds(subjectId));
    }

    // Removing one episode must leave the show's other in-progress episodes alone. This is the
    // behaviour that replaced the original series-wide collapse.
    [Fact]
    public void ResumeQuery_OverriddenEpisode_HidesOnlyThatEpisode()
    {
        var removedId = Guid.NewGuid();
        var keptOneId = Guid.NewGuid();
        var keptTwoId = Guid.NewGuid();
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var series = AddSeries(ctx, Guid.NewGuid(), "Show");

            foreach (var id in new[] { removedId, keptOneId, keptTwoId })
            {
                var episode = AddEpisode(ctx, id, series);
                AddUserData(ctx, episode, user, positionTicks: 1000, lastPlayedDate: _played);
            }

            AddOverride(ctx, user, removedId, _laterThanPlayed);
            ctx.SaveChanges();
        }

        // Both sides are ordered: the ids are random, so declaration order means nothing.
        Assert.Equal(new[] { keptOneId, keptTwoId }.Order(), ResumeIds(userId).Order());
    }

    // Hiding a series from Next Up must not disturb the resume list.
    [Fact]
    public void ResumeQuery_SeriesHiddenFromNextUp_KeepsEpisodesInResume()
    {
        var episodeId = Guid.NewGuid();
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var series = AddSeries(ctx, Guid.NewGuid(), "Show");
            var episode = AddEpisode(ctx, episodeId, series);
            AddUserData(ctx, episode, user, positionTicks: 1000, lastPlayedDate: _played);

            AddOverride(ctx, user, series.Id, _laterThanPlayed);
            ctx.SaveChanges();
        }

        Assert.Equal([episodeId], ResumeIds(userId));
    }

    [Fact]
    public void ResumeQuery_OverriddenEpisodePlayedSince_IsVisible()
    {
        var episodeId = Guid.NewGuid();
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var series = AddSeries(ctx, Guid.NewGuid(), "Show");
            var episode = AddEpisode(ctx, episodeId, series);
            AddUserData(ctx, episode, user, positionTicks: 1000, lastPlayedDate: _laterThanPlayed);
            AddOverride(ctx, user, episodeId, _played);
            ctx.SaveChanges();
        }

        Assert.Equal([episodeId], ResumeIds(userId));
    }

    // Playing a sibling episode must not resurrect a specific episode the user removed.
    [Fact]
    public void ResumeQuery_SiblingEpisodePlayedSince_KeepsRemovedEpisodeHidden()
    {
        var removedId = Guid.NewGuid();
        var siblingId = Guid.NewGuid();
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var series = AddSeries(ctx, Guid.NewGuid(), "Show");

            var removed = AddEpisode(ctx, removedId, series);
            AddUserData(ctx, removed, user, positionTicks: 1000, lastPlayedDate: _earlierThanPlayed);

            var sibling = AddEpisode(ctx, siblingId, series);
            AddUserData(ctx, sibling, user, positionTicks: 1000, lastPlayedDate: _laterThanPlayed);

            AddOverride(ctx, user, removedId, _played);
            ctx.SaveChanges();
        }

        Assert.Equal([siblingId], ResumeIds(userId));
    }

    [Fact]
    public void NextUpKeys_HiddenSeries_IsExcluded()
    {
        Guid userId;
        string seriesKey;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;
            var series = AddSeries(ctx, Guid.NewGuid(), "Show");
            seriesKey = series.PresentationUniqueKey!;
            var episode = AddEpisode(ctx, Guid.NewGuid(), series);
            AddUserData(ctx, episode, user, positionTicks: 0, lastPlayedDate: _played, played: true);
            ctx.SaveChanges();

            // Sanity: without an override the series is a Next Up candidate.
            Assert.Equal([seriesKey], NextUpKeys(user));

            AddOverride(ctx, user, series.Id, _laterThanPlayed);
            ctx.SaveChanges();
        }

        using (var ctx = CreateDbContext())
        {
            var user = ctx.Users.First(u => u.Id.Equals(userId));
            Assert.Empty(NextUpKeys(user));
        }
    }

    // An episode removed from resume must not take its series out of Next Up.
    [Fact]
    public void NextUpKeys_EpisodeHiddenFromResume_SeriesStillListed()
    {
        using var ctx = CreateDbContext();
        var user = AddUser(ctx);
        var series = AddSeries(ctx, Guid.NewGuid(), "Show");
        var episode = AddEpisode(ctx, Guid.NewGuid(), series);
        AddUserData(ctx, episode, user, positionTicks: 1000, lastPlayedDate: _played, played: true);

        AddOverride(ctx, user, episode.Id, _laterThanPlayed);
        ctx.SaveChanges();

        Assert.Equal([series.PresentationUniqueKey!], NextUpKeys(user));
    }

    [Fact]
    public void NextUpKeys_EpisodePlayedSince_RestoresSeries()
    {
        using var ctx = CreateDbContext();
        var user = AddUser(ctx);
        var series = AddSeries(ctx, Guid.NewGuid(), "Show");

        var first = AddEpisode(ctx, Guid.NewGuid(), series);
        AddUserData(ctx, first, user, positionTicks: 0, lastPlayedDate: _earlierThanPlayed, played: true);

        var second = AddEpisode(ctx, Guid.NewGuid(), series);
        AddUserData(ctx, second, user, positionTicks: 0, lastPlayedDate: _laterThanPlayed, played: true);

        AddOverride(ctx, user, series.Id, _played);
        ctx.SaveChanges();

        Assert.Equal([series.PresentationUniqueKey!], NextUpKeys(user));
    }

    // Activity in an unrelated series must not lift the override.
    [Fact]
    public void NextUpKeys_OtherSeriesPlayedSince_KeepsSeriesHidden()
    {
        using var ctx = CreateDbContext();
        var user = AddUser(ctx);

        var hidden = AddSeries(ctx, Guid.NewGuid(), "Hidden");
        var hiddenEpisode = AddEpisode(ctx, Guid.NewGuid(), hidden);
        AddUserData(ctx, hiddenEpisode, user, positionTicks: 0, lastPlayedDate: _earlierThanPlayed, played: true);
        AddOverride(ctx, user, hidden.Id, _played);

        var other = AddSeries(ctx, Guid.NewGuid(), "Other");
        var otherEpisode = AddEpisode(ctx, Guid.NewGuid(), other);
        AddUserData(ctx, otherEpisode, user, positionTicks: 0, lastPlayedDate: _laterThanPlayed, played: true);

        ctx.SaveChanges();

        Assert.Equal([other.PresentationUniqueKey!], NextUpKeys(user));
    }

    // Foreign key enforcement has to be on for the cascade to mean anything. SQLite defaults it
    // off, so this is asserted rather than assumed.
    [Fact]
    public void Database_EnforcesForeignKeys()
    {
        using var ctx = CreateDbContext();
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_keys";
        ctx.Database.OpenConnection();

        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    // ItemPersistenceService.DeleteItem removes items with ExecuteDelete, which bypasses the change
    // tracker, and it does not clear this table by hand. So the cascade has to be enforced by the
    // database itself; a test deleting through the tracker would pass while production leaked rows.
    [Fact]
    public void Override_ItemDeletedOutsideChangeTracker_RowIsRemoved()
    {
        Guid movieId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            var movie = AddMovie(ctx, Guid.NewGuid());
            movieId = movie.Id;
            AddOverride(ctx, user, movie.Id, _played);
            ctx.SaveChanges();
        }

        using (var ctx = CreateDbContext())
        {
            ctx.BaseItems.Where(e => e.Id.Equals(movieId)).ExecuteDelete();
        }

        using (var ctx = CreateDbContext())
        {
            Assert.Empty(ctx.UserItemResumeOverrides);
        }
    }

    [Fact]
    public void Override_ItemDeleted_RowIsRemoved()
    {
        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            var movie = AddMovie(ctx, Guid.NewGuid());
            AddOverride(ctx, user, movie.Id, _played);
            ctx.SaveChanges();
        }

        using (var ctx = CreateDbContext())
        {
            ctx.BaseItems.RemoveRange(ctx.BaseItems.Where(e => e.Type == _itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie]));
            ctx.SaveChanges();
        }

        using (var ctx = CreateDbContext())
        {
            Assert.Empty(ctx.UserItemResumeOverrides);
        }
    }

    [Fact]
    public void Override_UserDeleted_RowIsRemoved()
    {
        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            var movie = AddMovie(ctx, Guid.NewGuid());
            AddOverride(ctx, user, movie.Id, _played);
            ctx.SaveChanges();
        }

        using (var ctx = CreateDbContext())
        {
            ctx.Users.RemoveRange(ctx.Users);
            ctx.SaveChanges();
        }

        using (var ctx = CreateDbContext())
        {
            Assert.Empty(ctx.UserItemResumeOverrides);
        }
    }

    // A real library gives each item several UserData rows (one per CustomDataKey) and hangs
    // everything off a library folder through AncestorIds. The first version of this feature
    // composed the override lookup into the resume query as a correlated subquery, which was fine
    // against one flat user data row per item and killed the server process against this shape.
    [Fact]
    public void ResumeQuery_ProductionShapedLibrary_HidesOverriddenItem()
    {
        var visibleMovieId = Guid.NewGuid();
        var visibleEpisodeId = Guid.NewGuid();
        Guid userId;

        using (var ctx = CreateDbContext())
        {
            var user = AddUser(ctx);
            userId = user.Id;

            var library = new BaseItemEntity
            {
                Id = _libraryId,
                Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Folder],
                Name = "Library",
                IsFolder = true,
                IsVirtualItem = false
            };
            ctx.BaseItems.Add(library);

            var overriddenMovie = AddMovie(ctx, Guid.NewGuid());
            var visibleMovie = AddMovie(ctx, visibleMovieId);
            var series = AddSeries(ctx, Guid.NewGuid(), "Show");
            var episode = AddEpisode(ctx, visibleEpisodeId, series);

            foreach (var item in new[] { overriddenMovie, visibleMovie, series, episode })
            {
                ctx.AncestorIds.Add(new AncestorId { ItemId = item.Id, ParentItemId = _libraryId, Item = item, ParentItem = library });
            }

            // Three user data rows per item, as the real schema produces.
            foreach (var item in new[] { overriddenMovie, visibleMovie, episode })
            {
                for (var i = 0; i < 3; i++)
                {
                    ctx.UserData.Add(new UserData
                    {
                        ItemId = item.Id,
                        Item = item,
                        UserId = user.Id,
                        User = user,
                        CustomDataKey = $"{item.Id:N}-{i}",
                        PlaybackPositionTicks = 1000,
                        LastPlayedDate = _played,
                        Played = false
                    });
                }
            }

            AddOverride(ctx, user, overriddenMovie.Id, _laterThanPlayed);

            // A next up override on the series must leave its episode in the resume list.
            AddOverride(ctx, user, series.Id, _laterThanPlayed);
            ctx.SaveChanges();
        }

        Assert.Equal(new[] { visibleMovieId, visibleEpisodeId }.Order(), ResumeIds(userId).Order());
    }

    private List<Guid> ResumeIds(Guid userId)
    {
        using var ctx = CreateDbContext();
        var user = ctx.Users.First(u => u.Id.Equals(userId));
        return _repository.GetItemList(new InternalItemsQuery(user)
        {
            IsResumable = true,
            IncludeOwnedItems = true
        }).Select(i => i.Id).ToList();
    }

    private IReadOnlyList<string> NextUpKeys(User user)
    {
        return _nextUpService.GetNextUpSeriesKeys(
            new InternalItemsQuery(user) { TopParentIds = [_libraryId] },
            DateTime.MinValue);
    }

    private static User AddUser(JellyfinDbContext ctx, string name = "test")
    {
        var user = new User(name, "auth-provider", "reset-provider");
        ctx.Users.Add(user);
        return user;
    }

    private BaseItemEntity AddMovie(JellyfinDbContext ctx, Guid id)
    {
        var movie = new BaseItemEntity
        {
            Id = id,
            Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie],
            Name = "Movie",
            PresentationUniqueKey = id.ToString("N"),
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false,
            TopParentId = _libraryId
        };

        ctx.BaseItems.Add(movie);
        return movie;
    }

    private BaseItemEntity AddSeries(JellyfinDbContext ctx, Guid id, string name)
    {
        var series = new BaseItemEntity
        {
            Id = id,
            Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Series],
            Name = name,
            PresentationUniqueKey = id.ToString("N"),
            SeriesPresentationUniqueKey = id.ToString("N"),
            IsSeries = true,
            IsFolder = true,
            IsVirtualItem = false,
            TopParentId = _libraryId
        };

        ctx.BaseItems.Add(series);
        return series;
    }

    private BaseItemEntity AddEpisode(JellyfinDbContext ctx, Guid id, BaseItemEntity series)
    {
        var episode = new BaseItemEntity
        {
            Id = id,
            Type = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Episode],
            Name = "Episode",
            PresentationUniqueKey = id.ToString("N"),
            SeriesPresentationUniqueKey = series.SeriesPresentationUniqueKey,
            SeriesId = series.Id,
            ParentIndexNumber = 1,
            MediaType = "Video",
            IsFolder = false,
            IsVirtualItem = false,
            TopParentId = _libraryId
        };

        ctx.BaseItems.Add(episode);
        return episode;
    }

    private static void AddUserData(
        JellyfinDbContext ctx,
        BaseItemEntity item,
        User user,
        long positionTicks,
        DateTime? lastPlayedDate,
        bool played = false)
    {
        ctx.UserData.Add(new UserData
        {
            ItemId = item.Id,
            Item = item,
            UserId = user.Id,
            User = user,
            CustomDataKey = item.Id.ToString("N"),
            PlaybackPositionTicks = positionTicks,
            LastPlayedDate = lastPlayedDate,
            Played = played
        });
    }

    private static void AddOverride(JellyfinDbContext ctx, User user, Guid targetId, DateTime overriddenAt)
    {
        ctx.UserItemResumeOverrides.Add(new UserItemResumeOverride
        {
            UserId = user.Id,
            User = user,
            ItemId = targetId,
            Item = null,
            OverriddenAt = overriddenAt
        });
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}

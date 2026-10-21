using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class NextEpisodeHealthCheckTests : IAsyncLifetime
{
    private const long EpisodeSize = 1_000_000_000;
    private static readonly IReadOnlyDictionary<Guid, string[]> NoLinks = new Dictionary<Guid, string[]>();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DavDatabaseContext _context = null!;
    private DavItem _category = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _context = new DavDatabaseContext(
            new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();
        _category = Add(Directory("tv", "/content/tv", parent: null, historyItemId: null));
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SeasonPack_ReturnsNextFileInPack()
    {
        var pack = AddRelease("Example.Show.S01.1080p.WEB");
        var playing = AddFile(pack, "Example.Show.S01E01.1080p.WEB.mkv");
        AddFile(pack, "Example.Show.S01E03.1080p.WEB.mkv");
        var next = AddFile(pack, "Example.Show.S01E02.1080p.WEB.mkv");

        Assert.Equal(next.Id, (await FindNextAsync(playing))?.Id);
    }

    [Fact]
    public async Task SiblingRelease_MatchesSameShowOnly_AndUsesFolderTagForObfuscatedNames()
    {
        var playing = AddFile(AddRelease("Example.Show.S01E01.1080p.WEB"), "Example.Show.S01E01.1080p.WEB.mkv");
        AddFile(AddRelease("Other.Show.S01E02.1080p.WEB"), "Other.Show.S01E02.1080p.WEB.mkv");
        var next = AddFile(AddRelease("Example.Show.S01E02.720p.HDTV"), "a1b2c3d4e5.mkv");

        Assert.Equal(next.Id, (await FindNextAsync(playing))?.Id);
    }

    [Fact]
    public async Task HistoryClearedByArr_StillFindsSiblingReleasesAndNestedFiles()
    {
        // Sonarr's "Remove Completed" clears HistoryItemId on every imported release.
        var playing = AddFile(
            AddRelease("Example.Show.S01E07.1080p.WEB", historyCleared: true), "Example.Show.S01E07.1080p.WEB.mkv");
        var nested = AddFolder(AddRelease("Example.Show.S01E08.1080p.WEB", historyCleared: true), "Example.Show.S01E08");
        var next = AddFile(nested, "Example.Show.S01E08.1080p.WEB.mkv");
        AddFile(AddRelease("Example.Show.S01E09.1080p.WEB", historyCleared: true), "Example.Show.S01E09.1080p.WEB.mkv");

        Assert.Equal(next.Id, (await FindNextAsync(playing))?.Id);
    }

    [Fact]
    public async Task LastEpisodeOfSeason_RollsToFirstAvailableEpisodeOfLaterSeason()
    {
        var playing = AddFile(AddRelease("Example.Show.S01E10.1080p.WEB"), "Example.Show.S01E10.1080p.WEB.mkv");
        AddFile(AddRelease("Example.Show.S01E09.1080p.WEB"), "Example.Show.S01E09.1080p.WEB.mkv");
        var pack = AddRelease("Example.Show.S02.1080p.WEB");
        AddFile(pack, "Example.Show.S02E03.1080p.WEB.mkv");
        var next = AddFile(pack, "Example.Show.S02E02.1080p.WEB.mkv");

        Assert.Equal(next.Id, (await FindNextAsync(playing))?.Id);
    }

    [Fact]
    public async Task MultiEpisodeFile_SkipsCoveredEpisodes()
    {
        var pack = AddRelease("Example.Show.S01.1080p.WEB");
        var playing = AddFile(pack, "Example.Show.S01E01-E02.1080p.WEB.mkv");
        var next = AddFile(pack, "Example.Show.S01E03.1080p.WEB.mkv");

        Assert.Equal(next.Id, (await FindNextAsync(playing))?.Id);
    }

    [Fact]
    public async Task SamplesAndNonMediaAreIgnored()
    {
        var playing = AddFile(AddRelease("Example.Show.S01E01.1080p.WEB"), "Example.Show.S01E01.1080p.WEB.mkv");
        var nextRelease = AddRelease("Example.Show.S01E02.1080p.WEB");
        AddFile(nextRelease, "Example.Show.S01E02.1080p.WEB.sample.mkv", size: 20_000_000);
        AddFile(nextRelease, "Example.Show.S01E02.1080p.WEB.nfo", size: 1_000);
        var next = AddFile(nextRelease, "Example.Show.S01E02.1080p.WEB.mkv");

        Assert.Equal(next.Id, (await FindNextAsync(playing))?.Id);
    }

    [Fact]
    public async Task DuplicateReleases_PreferLibraryLinkedCopy()
    {
        var playing = AddFile(AddRelease("Example.Show.S01E01.1080p.WEB"), "Example.Show.S01E01.1080p.WEB.mkv");
        var linked = AddFile(AddRelease("Example.Show.S01E02.1080p.WEB"), "Example.Show.S01E02.1080p.WEB.mkv");
        AddFile(AddRelease("Example.Show.S01E02.2160p.WEB"), "Example.Show.S01E02.2160p.WEB.mkv");
        await _context.SaveChangesAsync();

        var next = await NextEpisodeHealthCheckService.FindNextEpisodeAsync(
            _context, playing, new Dictionary<Guid, string[]> { [linked.Id] = ["/library/e02.mkv"] },
            CancellationToken.None);

        Assert.Equal(linked.Id, next?.Id);
    }

    [Theory]
    [InlineData("Example.Movie.2021.1080p.WEB", "Example.Movie.2021.1080p.WEB.mkv")]
    [InlineData("Example.Show.S01E05.1080p.WEB", "Example.Show.S01E05.1080p.WEB.mkv")]
    public async Task NoLaterEpisode_ReturnsNull(string release, string file)
    {
        var playing = AddFile(AddRelease(release), file);
        AddFile(AddRelease("Example.Show.S01E04.1080p.WEB"), "Example.Show.S01E04.1080p.WEB.mkv");

        Assert.Null(await FindNextAsync(playing));
    }

    [Fact]
    public void RecordRead_FiresOnceForSustainedReadPastThreshold()
    {
        var service = NewService();
        var item = NewFile("/content/tv/Example.Show.S01E01/Example.Show.S01E01.mkv");
        var now = DateTimeOffset.UtcNow;
        const long chunk = 8L * 1024 * 1024;

        // Players read the container index at the end of the file on open: small, must not fire.
        Assert.False(service.RecordRead(item, 2 * 1024 * 1024, EpisodeSize - 1, EpisodeSize, 50, now));
        // Sustained reads before the threshold never count.
        for (var i = 0; i < 10; i++)
            Assert.False(service.RecordRead(item, chunk, EpisodeSize / 10, EpisodeSize, 50, now));

        var fired = Enumerable.Range(0, 10)
            .Count(i => service.RecordRead(item, chunk, EpisodeSize * 6 / 10 + i * chunk, EpisodeSize, 50, now));
        Assert.Equal(1, fired);
        Assert.True(service.RecordRead(item, NextEpisodeHealthCheckService.SustainedReadBytes,
            EpisodeSize * 6 / 10, EpisodeSize, 50, now + NextEpisodeHealthCheckService.RefireInterval));
    }

    [Fact]
    public void RecordRead_ZeroPercentFiresAfterSustainedReadFromStart()
    {
        var service = NewService();
        var item = NewFile("/content/tv/Example.Show.S01E01/Example.Show.S01E01.mkv");

        Assert.False(service.RecordRead(item, 16L * 1024 * 1024, 0, EpisodeSize, 0, DateTimeOffset.UtcNow));
        Assert.True(service.RecordRead(item, 16L * 1024 * 1024, 16L * 1024 * 1024, EpisodeSize, 0, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RecordRead_IgnoresFilesOutsideContent()
    {
        var service = NewService();
        var item = NewFile("/nzbs/Example.Show.S01E01.mkv");

        Assert.False(service.RecordRead(item, 64L * 1024 * 1024, EpisodeSize - 1, EpisodeSize, 0, DateTimeOffset.UtcNow));
    }

    private static NextEpisodeHealthCheckService NewService()
    {
        var config = new ConfigManager();
        // RecordRead never touches the health-check service.
        return new NextEpisodeHealthCheckService(config, null!, new FilesLibraryIndex(config, TimeProvider.System));
    }

    private async Task<DavItem?> FindNextAsync(DavItem playing)
    {
        await _context.SaveChangesAsync();
        return await NextEpisodeHealthCheckService.FindNextEpisodeAsync(
            _context, playing, NoLinks, CancellationToken.None);
    }

    private DavItem AddRelease(string name, bool historyCleared = false) =>
        Add(Directory(name, $"{_category.Path}/{name}", _category.Id, historyCleared ? null : Guid.NewGuid()));

    private DavItem AddFolder(DavItem parent, string name) =>
        Add(Directory(name, $"{parent.Path}/{name}", parent.Id, parent.HistoryItemId));

    private DavItem AddFile(DavItem parent, string name, long size = EpisodeSize)
    {
        var file = NewFile($"{parent.Path}/{name}", parent.Id);
        file.FileSize = size;
        file.HistoryItemId = parent.HistoryItemId;
        return Add(file);
    }

    private DavItem Add(DavItem item)
    {
        _context.Items.Add(item);
        return item;
    }

    private static DavItem Directory(string name, string path, Guid? parent, Guid? historyItemId)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parent,
            Name = name,
            Type = DavItem.ItemType.Directory,
            SubType = DavItem.ItemSubType.Directory,
            Path = path,
            HistoryItemId = historyItemId,
        };
    }

    private static DavItem NewFile(string path, Guid? parentId = null)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = parentId,
            Name = path[(path.LastIndexOf('/') + 1)..],
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = path,
        };
    }
}

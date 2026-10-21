using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Once a series episode has been played past <c>repair.next-episode-check-percent</c>,
/// queues a priority health check of the next available episode so a dead file can be
/// repaired or replaced before playback reaches it.
/// </summary>
public sealed class NextEpisodeHealthCheckService
{
    // A sustained read, not a header probe or the container-index read players issue at
    // the end of an MKV on open.
    internal const long SustainedReadBytes = 32L * 1024 * 1024;
    internal static readonly TimeSpan RefireInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan RecentCheckWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);

    private readonly ConfigManager _configManager;
    private readonly HealthCheckService _healthCheckService;
    private readonly FilesLibraryIndex _filesLibraryIndex;
    private readonly IDbContextFactory<DavDatabaseContext>? _dbContextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, Progress> _progress = new();
    private long _lastPruneTicks;

    public NextEpisodeHealthCheckService(
        ConfigManager configManager,
        HealthCheckService healthCheckService,
        FilesLibraryIndex filesLibraryIndex,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory = null,
        TimeProvider? timeProvider = null)
    {
        _configManager = configManager;
        _healthCheckService = healthCheckService;
        _filesLibraryIndex = filesLibraryIndex;
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private sealed class Progress
    {
        public long Bytes;
        public DateTimeOffset LastReadAt;
        public DateTimeOffset? FiredAt;
    }

    /// <summary>Called for every chunk served to a client; cheap unless it fires.</summary>
    public void OnPlaybackRead(DavItem? item, long bytes, long position, long? fileSize)
    {
        // The item's own size: a /view range response is a length-limited, non-seekable wrapper,
        // so the stream cannot report the whole file's length.
        if (item is null || (item.FileSize ?? fileSize) is not { } size) return;
        if (!_configManager.IsNextEpisodeCheckEnabled()) return;
        if (!RecordRead(item, bytes, position, size, _configManager.GetNextEpisodeCheckPercent(),
                _timeProvider.GetUtcNow()))
            return;
        _ = Task.Run(() => QueueNextEpisodeAsync(item));
    }

    /// <summary>Returns true once per <see cref="RefireInterval"/> when the read qualifies.</summary>
    internal bool RecordRead(DavItem item, long bytes, long position, long fileSize, int percent, DateTimeOffset now)
    {
        if (bytes <= 0 || fileSize <= 0 || position < fileSize * percent / 100) return false;
        if (!item.Path.StartsWith("/content/", StringComparison.Ordinal)
            || !FilenameUtil.IsHealthCheckCandidate(item.Name))
            return false;

        PruneIfDue(now);
        var progress = _progress.GetOrAdd(item.Id, _ => new Progress());
        lock (progress)
        {
            progress.LastReadAt = now;
            if (progress.FiredAt is { } firedAt && now - firedAt < RefireInterval) return false;
            progress.Bytes += bytes;
            if (progress.Bytes < SustainedReadBytes) return false;
            progress.Bytes = 0;
            progress.FiredAt = now;
            return true;
        }
    }

    private void PruneIfDue(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.UtcTicks - last < PruneInterval.Ticks
            || Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) != last)
            return;
        foreach (var (id, progress) in _progress)
        {
            if (now - progress.LastReadAt >= RefireInterval)
                _progress.TryRemove(new KeyValuePair<Guid, Progress>(id, progress));
        }
    }

    private async Task QueueNextEpisodeAsync(DavItem playing)
    {
        try
        {
            using var cts = new CancellationTokenSource(LookupTimeout);
            await using var dbContext = DavDatabaseContexts.Create(null, _dbContextFactory);
            var next = await FindNextEpisodeAsync(
                    dbContext, playing, _filesLibraryIndex.GetSnapshot().Links, cts.Token)
                .ConfigureAwait(false);
            // Movies and the last available episode land here too, so this stays at Debug.
            if (next is null)
            {
                Log.Debug("Next-episode health check: no next episode found after {Path}", playing.Path);
                return;
            }

            if (next.LastHealthCheck is { } lastCheck && _timeProvider.GetUtcNow() - lastCheck < RecentCheckWindow)
            {
                Log.Debug(
                    "Next-episode health check: {NextPath} was already checked at {LastCheck}; skipping",
                    next.Path, lastCheck);
                return;
            }

            var outcome = await _healthCheckService
                .QueueFileRecheckAsync(next.Id, cts.Token, priority: true)
                .ConfigureAwait(false);
            if (outcome == HealthCheckService.FileRecheckOutcome.Queued)
                Log.Information("Playing {Path}; queued a health check of the next episode {NextPath}",
                    playing.Path, next.Path);
            else
                Log.Debug("Next-episode health check for {NextPath} not queued: {Outcome}", next.Path, outcome);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Next-episode health check skipped for {Path}. Reason: {Reason}",
                playing.Path, "lookup timed out");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (e.TryGetKnownErrorMessage(out var reason))
            {
                Log.Warning("Next-episode health check skipped for {Path}. Reason: {Reason}",
                    playing.Path, reason);
                Log.Debug(e, "Next-episode health check known failure stack");
            }
            else
            {
                Log.Error(e, "Unexpected error queuing next-episode health check for {Path}: {Message}",
                    playing.Path, e.Message);
            }
        }
    }

    private sealed record ReleaseFolder(Guid Id, string Name, Guid? ParentId);

    /// <summary>
    /// Finds the earliest episode after <paramref name="playing"/> among releases of the same
    /// show in the same <c>/content/&lt;category&gt;</c> folder: the next file of a season pack,
    /// a sibling single-episode release, or the first available episode of a later season.
    /// </summary>
    internal static async Task<DavItem?> FindNextEpisodeAsync(
        DavDatabaseContext dbContext,
        DavItem playing,
        IReadOnlyDictionary<Guid, string[]> linked,
        CancellationToken ct)
    {
        // "", "content", "<category>", "<release>", ..., "<file>"
        var segments = playing.Path.Split('/');
        if (segments.Length < 5 || segments[1] != "content") return null;
        var releasePath = string.Join('/', segments[..4]);
        var release = await dbContext.Items.AsNoTracking()
            .Where(item => item.Path == releasePath && item.Type == DavItem.ItemType.Directory)
            .Select(item => new ReleaseFolder(item.Id, item.Name, item.ParentId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (release is null) return null;
        if (EpisodeTagOf(playing.Name, release.Name) is not { Episode: { } playingEpisode } playingTag)
            return null;
        var after = (playingTag.Season, playingTag.EpisodeEnd ?? playingEpisode);

        var showTokens = FilenameMatcher.HeadTokens(release.Name);
        var releases = showTokens.Length == 0
            ? [release]
            : (await dbContext.Items.AsNoTracking()
                    .Where(item => item.ParentId == release.ParentId && item.Type == DavItem.ItemType.Directory)
                    .Select(item => new ReleaseFolder(item.Id, item.Name, item.ParentId))
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .Where(folder => FilenameMatcher.TokensEqual(showTokens, FilenameMatcher.HeadTokens(folder.Name)))
                .ToList();
        // Walk the release folders by ParentId. HistoryItemId is not usable here: it is cleared
        // when the Arr removes the completed download from history, but the folders stay.
        var releaseOf = releases.ToDictionary(folder => folder.Id);
        var frontier = releaseOf.Keys.Select(id => (Guid?)id).ToList();
        var files = new List<(DavItem File, ReleaseFolder Release)>();
        while (frontier.Count > 0)
        {
            var parentIds = frontier;
            var children = await dbContext.Items.AsNoTracking()
                .Where(item => parentIds.Contains(item.ParentId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            frontier = [];
            foreach (var child in children)
            {
                var owner = releaseOf[child.ParentId!.Value];
                if (child.Type == DavItem.ItemType.Directory)
                {
                    releaseOf[child.Id] = owner;
                    frontier.Add(child.Id);
                }
                else if (child.Type == DavItem.ItemType.UsenetFile && FilenameUtil.IsHealthCheckCandidate(child.Name))
                {
                    files.Add((child, owner));
                }
            }
        }

        DavItem? best = null;
        var bestKey = (Season: 0, Episode: 0);
        foreach (var group in files.GroupBy(entry => entry.Release))
        {
            var videos = group.Select(entry => entry.File).ToList();
            var releaseName = group.Key.Name;
            foreach (var file in videos)
            {
                if (file.Id == playing.Id
                    || VideoSelection.IsProbablyDecoy(file.Name)
                    || VideoSelection.IsSampleCandidate(file, videos))
                    continue;
                if (EpisodeTagOf(file.Name, releaseName) is not { Episode: { } episode } tag) continue;
                var key = (tag.Season, episode);
                if (key.CompareTo(after) <= 0) continue;
                if (best is null
                    || key.CompareTo(bestKey) < 0
                    || (key == bestKey && IsPreferred(file, best, linked)))
                {
                    best = file;
                    bestKey = key;
                }
            }
        }

        return best;
    }

    // A file's own SxxEyy wins; otherwise a single-episode release folder names it.
    private static FilenameMatcher.EpisodeTag? EpisodeTagOf(string fileName, string releaseName) =>
        FilenameMatcher.ParseEpisode(fileName) is { Episode: not null } fileTag
            ? fileTag
            : FilenameMatcher.ParseEpisode(releaseName) is { Episode: not null } releaseTag
                ? releaseTag
                : null;

    // Prefer the copy the library actually links (what the player will open next), then the newest.
    private static bool IsPreferred(DavItem candidate, DavItem current, IReadOnlyDictionary<Guid, string[]> linked)
    {
        var candidateLinked = linked.ContainsKey(candidate.Id);
        if (candidateLinked != linked.ContainsKey(current.Id)) return candidateLinked;
        return candidate.CreatedAt > current.CreatedAt;
    }
}

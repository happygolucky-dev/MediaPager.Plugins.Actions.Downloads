using MediaPager.App.PluginContracts;
using MediaPager.Plugins.Interface.Download;
using System.Text.Json;

namespace MediaPager.Plugins.Actions.Downloads;

public sealed class DownloadsPlugin(
    IPluginActivity activity,
    IPluginSettingsStore settingsStore,
    IMediaToolBroker mediaTools) :
    IPluginActions, IPluginSettingsSchema, IPluginNavigation, IDownloadProviderPlugin
{
    private const string DownloadSeasonActionId = "download-season";
    private const string DownloadSeriesActionId = "download-series";
    private const string SettingsKey = "downloads";
    private const string ScratchInCatalogSetting = "scratchInCatalog";
    private const string CatalogSettingPrefix = "catalog.";
    private static readonly MediaKind[] NonTvKinds =
    [
        MediaKind.Movie, MediaKind.Music, MediaKind.Podcast, MediaKind.Audiobook, MediaKind.Book,
    ];
    private static readonly JsonSerializerOptions SeriesEpisodeJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _transferQueue = new(1, 1);

    public PluginDescriptor Descriptor { get; } = new(
        "mediapager.actions.downloads",
        "Downloads",
        "0.1.0",
        "HappyGoLucky Dev",
        "Download media through loaded stream-provider plugins.");

    public IReadOnlyList<PluginActionDescriptor> Actions { get; } =
    [
        new("download", "Download", "download", PluginActionSurface.PosterCard,
            PluginActionPosition.TopRight, 100,
            NonTvKinds,
            Scope: PluginActionScope.StreamItem),
        new("download", "Download", "download", PluginActionSurface.DetailScreen,
            PluginActionPosition.Inline, 100,
            NonTvKinds,
            Scope: PluginActionScope.StreamItem),
        new("download", "Download", "download", PluginActionSurface.DetailScreen,
            PluginActionPosition.Inline, 100,
            [MediaKind.Tv],
            Scope: PluginActionScope.Episode,
            ShowLabel: false),
        new(DownloadSeasonActionId, "Download", "download", PluginActionSurface.DetailScreen,
            PluginActionPosition.Inline, 100,
            [MediaKind.Tv],
            Scope: PluginActionScope.Season,
            ConfirmationTitle: "Queue entire season?",
            ConfirmationMessage: "This will queue the entire season. Are you sure? Repeated requests may trigger provider restrictions."),
        new(DownloadSeriesActionId, "Download", "download", PluginActionSurface.DetailScreen,
            PluginActionPosition.Inline, 100,
            [MediaKind.Tv],
            Scope: PluginActionScope.Series,
            ConfirmationTitle: "Queue entire series?",
            ConfirmationMessage: "This will queue the entire series. Are you sure? You may get marked and IP banned if you do this."),
    ];

    public async Task<IReadOnlyList<PluginActionDescriptor>> GetActionsAsync(CancellationToken cancellationToken)
    {
        var available = new List<PluginActionDescriptor>();
        foreach (var descriptor in Actions)
        {
            foreach (var kind in descriptor.Kinds ?? Enum.GetValues<MediaKind>())
            {
                if (!await mediaTools.HasOnlineStreamProviderAsync(kind, cancellationToken))
                    continue;
                if (await HasConflictingStreamActionAsync(kind, cancellationToken))
                    continue;

                available.Add(descriptor with
                {
                    Kinds = [kind],
                    // Keep the action discoverable even before a destination has been selected.
                    // The queued job reports a useful setup error if the user tries it too early.
                    Enabled = true,
                });
            }
        }
        return available;
    }

    private Task<bool> HasConflictingStreamActionAsync(MediaKind kind, CancellationToken cancellationToken) =>
        mediaTools.HasOtherStreamItemActionAsync(Descriptor.Id, kind, cancellationToken);

    public IReadOnlyList<PluginSettingDefinition> Settings { get; } =
    [
        new("catalog.movies", "Movies catalog", PluginSettingType.Catalog, CatalogTypeSlug: "movies"),
        new("catalog.tv-shows", "TV Shows catalog", PluginSettingType.Catalog, CatalogTypeSlug: "tv-shows"),
        new("catalog.music", "Music catalog", PluginSettingType.Catalog, CatalogTypeSlug: "music"),
        new("catalog.podcasts", "Podcasts catalog", PluginSettingType.Catalog, CatalogTypeSlug: "podcasts"),
        new("catalog.audiobooks", "Audiobooks catalog", PluginSettingType.Catalog, CatalogTypeSlug: "audiobooks"),
        new("catalog.books", "Books catalog", PluginSettingType.Catalog, CatalogTypeSlug: "books"),
        new("advancedMode", "Advanced download settings", PluginSettingType.Boolean, Default: "false"),
        new("videoProfile", "Video profile", Default: "CopyToMp4"),
        new("audioProfile", "Audio profile", Default: "AacAudio"),
        new(ScratchInCatalogSetting, "Use the selected catalog folder for scratch files", PluginSettingType.Boolean, Default: "true"),
    ];

    public PluginSettingsNav? SettingsNav { get; } = new("Downloads", "download", "settings/index.html");

    public async Task<bool> IsAvailableAsync(MediaKind kind, CancellationToken cancellationToken = default)
    {
        var catalogTypeSlug = CatalogTypeSlug(kind);
        if (catalogTypeSlug is null) return false;

        var selected = await settingsStore.GetAsync(SettingsKey, $"{CatalogSettingPrefix}{catalogTypeSlug}", cancellationToken);
        if (!int.TryParse(selected, out var catalogId)) return false;

        return await mediaTools.IsDestinationAvailableAsync(catalogId, catalogTypeSlug, cancellationToken);
    }

    public Task InvokeActionAsync(PluginActionContext context, CancellationToken cancellationToken) =>
        context.ActionId.ToLowerInvariant() switch
        {
            DownloadSeriesActionId => QueueSeriesDownloadAsync(context),
            DownloadSeasonActionId => QueueSeasonDownloadAsync(context),
            _ => QueueDownloadAsync(context, cancellationToken),
        };

    public Task QueueDownloadAsync(PluginActionContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.ExternalId))
        {
            activity.Notify("Download unavailable", "This action has no provider id for the selected title.", PluginNotificationLevel.Warning);
            return Task.CompletedTask;
        }

        var title = string.IsNullOrWhiteSpace(context.Title) ? context.ExternalId : context.Title.Trim();
        SeriesEpisodeMetadata? seriesEpisode = null;
        if (context.Kind == MediaKind.Tv && context.Season is > 0 && context.Episode is > 0)
        {
            try
            {
                seriesEpisode = ReadSeriesEpisodes(context.Metadata)
                    .FirstOrDefault(episode => episode.SeasonNumber == context.Season && episode.EpisodeNumber == context.Episode);
            }
            catch (JsonException)
            {
                activity.Notify("Episode unavailable", "The episode details could not be read.", PluginNotificationLevel.Warning);
                return Task.CompletedTask;
            }
            if (seriesEpisode is null)
            {
                activity.Notify("Episode unavailable", "The episode details were not supplied.", PluginNotificationLevel.Warning);
                return Task.CompletedTask;
            }
        }

        var job = activity.BeginJob($"Download {title}");
        _ = Task.Run(() => ResolveAndDownloadAsync(job, context, title, seriesEpisode), CancellationToken.None);
        return Task.CompletedTask;
    }

    private Task QueueSeriesDownloadAsync(PluginActionContext context) =>
        QueueEpisodeBatchAsync(context, null, isSeries: true);

    private Task QueueSeasonDownloadAsync(PluginActionContext context) =>
        QueueEpisodeBatchAsync(context, context.Season, isSeries: false);

    private Task QueueEpisodeBatchAsync(PluginActionContext context, int? seasonNumber, bool isSeries)
    {
        if (!isSeries && seasonNumber is null)
        {
            activity.Notify("Season unavailable", "A season number is required.", PluginNotificationLevel.Warning);
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(context.ExternalId))
        {
            activity.Notify("TV item unavailable", "This item has no provider id.", PluginNotificationLevel.Warning);
            return Task.CompletedTask;
        }

        SeriesEpisodeMetadata[] episodes;
        try
        {
            episodes = ReadSeriesEpisodes(context.Metadata)
                .Where(episode => seasonNumber is null || episode!.SeasonNumber == seasonNumber.Value)
                .OrderBy(episode => episode!.SeasonNumber)
                .ThenBy(episode => episode!.EpisodeNumber)
                .ToArray();
        }
        catch (JsonException)
        {
            activity.Notify("TV item unavailable", "The episode list could not be read.", PluginNotificationLevel.Warning);
            return Task.CompletedTask;
        }
        if (episodes.Length == 0)
        {
            activity.Notify("TV item unavailable", "No episode list was supplied for this item.", PluginNotificationLevel.Warning);
            return Task.CompletedTask;
        }

        var title = string.IsNullOrWhiteSpace(context.Title) ? context.ExternalId : context.Title.Trim();
        var jobTitle = isSeries ? $"Download series {title}" : $"Download season {seasonNumber} - {title}";
        var job = activity.BeginJob(jobTitle);
        _ = Task.Run(() => ResolveAndDownloadSeriesAsync(job, context, title, episodes), CancellationToken.None);
        return Task.CompletedTask;
    }

    private static SeriesEpisodeMetadata[] ReadSeriesEpisodes(IReadOnlyList<ContentMetadata>? metadata) =>
        (metadata ?? [])
            .Where(entry => string.Equals(entry.Label, "series-episode", StringComparison.OrdinalIgnoreCase))
            .Select(entry => JsonSerializer.Deserialize<SeriesEpisodeMetadata>(entry.Value, SeriesEpisodeJsonOptions))
            .Where(episode => episode is { SeasonNumber: > 0, EpisodeNumber: > 0 })
            .Cast<SeriesEpisodeMetadata>()
            .ToArray();

    private async Task ResolveAndDownloadAsync(
        IPluginJob job,
        PluginActionContext context,
        string title,
        SeriesEpisodeMetadata? seriesEpisode = null)
    {
        var acquired = false;
        try
        {
            await _transferQueue.WaitAsync(job.CancellationToken);
            acquired = true;
            job.Report(0, "Resolving stream…");
            var report = new Progress<MediaToolProgress>(update => job.Report(update.Progress, update.Message));
            await ResolveAndTransferAsync(job, context, title, report, seriesEpisode);
            job.Complete("Download complete.");
        }
        catch (OperationCanceledException)
        {
            // The host activity store marks the job cancelled and drives this token.
        }
        catch (Exception exception)
        {
            ReportDownloadFailure(job, title, exception);
        }
        finally
        {
            if (acquired) _transferQueue.Release();
        }
    }

    private async Task ResolveAndDownloadSeriesAsync(
        IPluginJob job,
        PluginActionContext seriesContext,
        string seriesTitle,
        IReadOnlyList<SeriesEpisodeMetadata> episodes)
    {
        var acquired = false;
        try
        {
            await _transferQueue.WaitAsync(job.CancellationToken);
            acquired = true;
            for (var index = 0; index < episodes.Count; index++)
            {
                var episode = episodes[index];
                var episodeIndex = index;
                var episodeTitle = $"{seriesTitle} - S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2} - {episode.Title}";
                var context = seriesContext with
                {
                    ActionId = "download",
                    Title = episodeTitle,
                    Season = episode.SeasonNumber,
                    Episode = episode.EpisodeNumber,
                    ImageUrl = episode.ImageUrl ?? seriesContext.ImageUrl,
                    Overview = episode.Overview ?? seriesContext.Overview,
                    Year = episode.Year ?? seriesContext.Year,
                    Metadata = null,
                };
                job.Report((double)index / episodes.Count,
                    $"Resolving S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2} ({index + 1}/{episodes.Count})…");
                var report = new Progress<MediaToolProgress>(update => job.Report(
                    (episodeIndex + Math.Clamp(update.Progress, 0, 1)) / episodes.Count,
                    $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}: {update.Message}"));
                await ResolveAndTransferAsync(job, context, episodeTitle, report, episode);
            }
            job.Complete($"Completed all {episodes.Count} episodes.");
        }
        catch (OperationCanceledException)
        {
            // The host activity store marks the job cancelled and drives this token.
        }
        catch (Exception exception)
        {
            ReportDownloadFailure(job, seriesTitle, exception);
        }
        finally
        {
            if (acquired) _transferQueue.Release();
        }
    }

    private async Task ResolveAndTransferAsync(
        IPluginJob job,
        PluginActionContext context,
        string title,
        IProgress<MediaToolProgress> report,
        SeriesEpisodeMetadata? seriesEpisode = null)
    {
        var resolved = await mediaTools.ResolveStreamAsync(context, job.CancellationToken);
        if (resolved is null)
            throw new InvalidOperationException("The stream provider did not return a safe HTTPS stream.");
        var kind = resolved.Kind;

        var catalogTypeSlug = CatalogTypeSlug(kind);
        var catalogSetting = catalogTypeSlug is null ? null : $"{CatalogSettingPrefix}{catalogTypeSlug}";
        var selectedCatalog = catalogSetting is null
            ? null
            : await settingsStore.GetAsync(SettingsKey, catalogSetting, job.CancellationToken);
        if (!int.TryParse(selectedCatalog, out var catalogId))
            throw new InvalidOperationException($"Select a {kind} catalog in Settings → Plugins → Downloads first.");

        var scratchInCatalog = !bool.TryParse(
            await settingsStore.GetAsync(SettingsKey, ScratchInCatalogSetting, job.CancellationToken), out var configuredScratch)
            || configuredScratch;
        var isAudio = kind is MediaKind.Music or MediaKind.Podcast or MediaKind.Audiobook or MediaKind.Book;
        var profileSetting = isAudio ? "audioProfile" : "videoProfile";
        var defaultProfile = isAudio ? MediaTranscodeProfile.AacAudio : MediaTranscodeProfile.CopyToMp4;
        var advancedMode = bool.TryParse(
            await settingsStore.GetAsync(SettingsKey, "advancedMode", job.CancellationToken), out var configuredAdvancedMode)
            && configuredAdvancedMode;
        var configuredProfile = advancedMode
            ? await settingsStore.GetAsync(SettingsKey, profileSetting, job.CancellationToken)
            : null;
        var profile = Enum.TryParse<MediaTranscodeProfile>(configuredProfile, ignoreCase: true, out var parsedProfile) &&
                      Enum.IsDefined(parsedProfile)
            ? parsedProfile
            : defaultProfile;

        await mediaTools.TransferAsync(new MediaTransferRequest(
            resolved.UpstreamUri,
            catalogId,
            kind,
            title,
            seriesEpisode is null ? context.ExternalId : $"{context.ExternalId}:S{seriesEpisode.SeasonNumber}E{seriesEpisode.EpisodeNumber}",
            context.ImageUrl,
            context.BackdropUrl,
            context.Overview,
            context.Year,
            scratchInCatalog,
            profile,
            job.Id)
        {
            MusicMetadata = context.MusicMetadata,
            SeriesTitle = seriesEpisode is null ? null : seriesEpisode.SeriesTitle,
            SeriesExternalId = seriesEpisode is null ? null : context.ExternalId,
            SeriesImageUrl = seriesEpisode is null ? null : seriesEpisode.SeriesImageUrl,
            SeriesBackdropUrl = seriesEpisode is null ? null : seriesEpisode.SeriesBackdropUrl,
            SeriesOverview = seriesEpisode is null ? null : seriesEpisode.SeriesOverview,
            SeriesYear = seriesEpisode is null ? null : seriesEpisode.SeriesYear,
            SeasonTitle = seriesEpisode?.SeasonTitle,
            SeasonNumber = seriesEpisode?.SeasonNumber,
            EpisodeNumber = seriesEpisode?.EpisodeNumber,
        }, report, job.CancellationToken);
    }

    private static string? CatalogTypeSlug(MediaKind kind) => kind switch
    {
        MediaKind.Movie => "movies",
        MediaKind.Tv => "tv-shows",
        MediaKind.Music => "music",
        MediaKind.Podcast => "podcasts",
        MediaKind.Audiobook => "audiobooks",
        MediaKind.Book => "books",
        _ => null,
    };

    private void ReportDownloadFailure(IPluginJob job, string title, Exception exception)
    {
        var error = exception is PluginOperationException pluginException
            ? pluginException.Error
            : new PluginError("downloads.failed", exception.Message);
        var titledError = error with { Message = $"{title}: {error.Message}" };
        job.FailWithError(titledError);
        activity.NotifyError("Download failed", titledError, PluginNotificationLevel.Error);
        activity.ClearJob(job);
    }

    private sealed record SeriesEpisodeMetadata(
        int SeasonNumber,
        int EpisodeNumber,
        string Title,
        string? ImageUrl,
        string? Overview,
        int? Year,
        string? SeasonTitle,
        string SeriesTitle,
        string? SeriesImageUrl,
        string? SeriesBackdropUrl,
        string? SeriesOverview,
        int? SeriesYear);

}

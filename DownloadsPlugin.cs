using MediaPager.App.PluginContracts;
using MediaPager.Plugins.Interface.Download;

namespace MediaPager.Plugins.Actions.Downloads;

public sealed class DownloadsPlugin(
    IPluginActivity activity,
    IPluginSettingsStore settingsStore,
    IMediaToolBroker mediaTools) :
    IPluginActions, IPluginSettingsSchema, IPluginNavigation, IDownloadProviderPlugin
{
    private const string SettingsKey = "downloads";
    private const string ScratchInCatalogSetting = "scratchInCatalog";
    private const string CatalogSettingPrefix = "catalog.";

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
            [MediaKind.Movie, MediaKind.Tv, MediaKind.Music, MediaKind.Podcast, MediaKind.Audiobook, MediaKind.Book],
            Scope: PluginActionScope.StreamItem),
        new("download", "Download", "download", PluginActionSurface.DetailScreen,
            PluginActionPosition.TopRight, 100,
            [MediaKind.Movie, MediaKind.Tv, MediaKind.Music, MediaKind.Podcast, MediaKind.Audiobook, MediaKind.Book],
            Scope: PluginActionScope.StreamItem),
    ];

    public async Task<IReadOnlyList<PluginActionDescriptor>> GetActionsAsync(CancellationToken cancellationToken)
    {
        var available = new List<PluginActionDescriptor>();
        foreach (var descriptor in Actions)
        {
            foreach (var kind in descriptor.Kinds ?? Enum.GetValues<MediaKind>())
            {
                if (await HasSpecializedDownloadActionAsync(kind, cancellationToken))
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

    private async Task<bool> HasSpecializedDownloadActionAsync(MediaKind kind, CancellationToken cancellationToken)
        => await mediaTools.HasSpecializedDownloadActionAsync(kind, cancellationToken);

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
        QueueDownloadAsync(context, cancellationToken);

    public Task QueueDownloadAsync(PluginActionContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.ExternalId))
        {
            activity.Notify("Download unavailable", "This action has no provider id for the selected title.", PluginNotificationLevel.Warning);
            return Task.CompletedTask;
        }

        var title = string.IsNullOrWhiteSpace(context.Title) ? context.ExternalId : context.Title.Trim();
        var job = activity.BeginJob($"Download {title}");
        _ = Task.Run(() => ResolveAndDownloadAsync(job, context, title), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ResolveAndDownloadAsync(IPluginJob job, PluginActionContext context, string title)
    {
        try
        {
            job.Report(0, "Resolving stream…");
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

            var report = new Progress<MediaToolProgress>(update => job.Report(update.Progress, update.Message));
            await mediaTools.TransferAsync(new MediaTransferRequest(
                resolved.UpstreamUri,
                catalogId,
                kind,
                title,
                context.ExternalId,
                context.ImageUrl,
                context.BackdropUrl,
                context.Overview,
                context.Year,
                scratchInCatalog,
                profile,
                job.Id)
            {
                MusicMetadata = context.MusicMetadata
            }, report, job.CancellationToken);
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

}

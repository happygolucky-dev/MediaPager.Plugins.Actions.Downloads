using System.Diagnostics;
using MediaPager.App.PluginContracts;
using MediaPager.Plugins.Interface.Download;

namespace MediaPager.Plugins.Actions.Downloads;

public sealed class DownloadsPlugin(
    IPluginHost pluginHost,
    IPluginActivity activity,
    IPluginSettingsStore settingsStore,
    ILibraryStore libraryStore) :
    IPluginActions, IPluginSettingsSchema, IPluginNavigation, IDownloadProviderPlugin
{
    private const string SettingsKey = "downloads";
    private const string ScratchInCatalogSetting = "scratchInCatalog";
    private const string CatalogSettingPrefix = "catalog.";
    private const int MaxConcurrent = 3;
    private readonly object concurrencyGate = new();
    private int activeJobs;

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
                var isAvailable = await IsAvailableAsync(kind, cancellationToken);
                available.Add(descriptor with
                {
                    Kinds = [kind],
                    Enabled = isAvailable,
                    AvailabilityMessage = isAvailable ? null : "Choose a destination catalog in Downloads settings before downloading.",
                    AvailabilityPluginId = isAvailable ? null : Descriptor.Id,
                });
            }
        }
        return available;
    }

    public IReadOnlyList<PluginSettingDefinition> Settings { get; } =
    [
        new("catalog.movies", "Movies catalog", PluginSettingType.Catalog, CatalogTypeSlug: "movies"),
        new("catalog.tv-shows", "TV Shows catalog", PluginSettingType.Catalog, CatalogTypeSlug: "tv-shows"),
        new("catalog.music", "Music catalog", PluginSettingType.Catalog, CatalogTypeSlug: "music"),
        new("catalog.podcasts", "Podcasts catalog", PluginSettingType.Catalog, CatalogTypeSlug: "podcasts"),
        new("catalog.audiobooks", "Audiobooks catalog", PluginSettingType.Catalog, CatalogTypeSlug: "audiobooks"),
        new("catalog.books", "Books catalog", PluginSettingType.Catalog, CatalogTypeSlug: "books"),
        new(ScratchInCatalogSetting, "Use the selected catalog folder for scratch files", PluginSettingType.Boolean, Default: "true"),
    ];

    public PluginSettingsNav? SettingsNav { get; } = new("Downloads", "download");

    public async Task<bool> IsAvailableAsync(MediaKind kind, CancellationToken cancellationToken = default)
    {
        var catalogTypeSlug = CatalogTypeSlug(kind);
        if (catalogTypeSlug is null) return false;

        var selected = await settingsStore.GetAsync(SettingsKey, $"{CatalogSettingPrefix}{catalogTypeSlug}", cancellationToken);
        if (!int.TryParse(selected, out var catalogId)) return false;

        var catalog = await libraryStore.GetCatalogAsync(catalogId, cancellationToken);
        return catalog is not null &&
               string.Equals(catalog.CatalogTypeSlug, catalogTypeSlug, StringComparison.OrdinalIgnoreCase) &&
               NormalizeDirectory(catalog.Path) is not null;
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
            var (provider, source) = FindProvider(context);
            if (provider is null || source is null)
                throw new InvalidOperationException("No loaded stream provider matches this title.");

            var resolved = await provider.ResolveAsync(
                new StreamResolveRequest(source.Key, context.ExternalId!, source.Kind, context.Season, context.Episode),
                job.CancellationToken);
            if (resolved?.UpstreamUri is not { Scheme: "https" } streamUri)
                throw new InvalidOperationException("The stream provider did not return a safe HTTPS stream.");

            var catalogTypeSlug = CatalogTypeSlug(source.Kind);
            var catalogSetting = catalogTypeSlug is null ? null : $"{CatalogSettingPrefix}{catalogTypeSlug}";
            var selectedCatalog = catalogSetting is null
                ? null
                : await settingsStore.GetAsync(SettingsKey, catalogSetting, job.CancellationToken);
            if (!int.TryParse(selectedCatalog, out var catalogId))
                throw new InvalidOperationException($"Select a {source.Kind} catalog in Settings → Plugins → Downloads first.");

            var catalog = await libraryStore.GetCatalogAsync(catalogId, job.CancellationToken);
            if (catalog is null || !string.Equals(catalog.CatalogTypeSlug, catalogTypeSlug, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected catalog no longer exists. Choose another catalog in Downloads settings.");
            var finalDirectory = NormalizeDirectory(catalog.Path)
                ?? throw new InvalidOperationException("The selected catalog has no folder location.");
            var scratchInCatalog = !bool.TryParse(
                await settingsStore.GetAsync(SettingsKey, ScratchInCatalogSetting, job.CancellationToken), out var configuredScratch)
                || configuredScratch;
            var scratchDirectory = scratchInCatalog
                ? finalDirectory
                : Path.Combine(Path.GetTempPath(), "MediaPager", "scratch");

            var libraryItem = await libraryStore.SaveAsync(new LibraryItemWriteRequest(
                CatalogItemId: null,
                CatalogTypeId: catalog.CatalogTypeId,
                Kind: source.Kind,
                Title: title,
                ExternalId: context.ExternalId,
                ImageUrl: context.ImageUrl,
                BackdropUrl: context.BackdropUrl,
                Overview: context.Overview,
                Year: context.Year,
                CatalogId: catalog.Id), job.CancellationToken);
            await RunDownloadAsync(job, streamUri, finalDirectory, scratchDirectory, title, libraryItem);
        }
        catch (OperationCanceledException)
        {
            // The host activity store marks the job cancelled and drives this token.
        }
        catch (Exception exception)
        {
            job.Fail(exception.Message);
            activity.Notify("Download failed", $"{title}: {exception.Message}", PluginNotificationLevel.Error);
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

    private (IStreamProviderPlugin? Provider, SourceDescriptor? Source) FindProvider(PluginActionContext context)
    {
        foreach (var provider in pluginHost.GetPlugins<IStreamProviderPlugin>())
        {
            var source = !string.IsNullOrWhiteSpace(context.SourceKey)
                ? provider.Sources.FirstOrDefault(candidate => string.Equals(candidate.Key, context.SourceKey, StringComparison.OrdinalIgnoreCase))
                : provider.Mode == StreamProviderMode.Online
                    ? provider.Sources.FirstOrDefault(candidate => context.Kind is null || candidate.Kind == context.Kind)
                    : null;
            if (source is not null) return (provider, source);
        }
        return (null, null);
    }

    private async Task RunDownloadAsync(
        IPluginJob job,
        Uri streamUri,
        string finalDirectory,
        string scratchDirectory,
        string title,
        LibraryItemHandle libraryItem)
    {
        var acquired = false;
        string? scratchPath = null;
        string? finalPath = null;
        try
        {
            await WaitForSlotAsync(MaxConcurrent, job.CancellationToken);
            acquired = true;
            Directory.CreateDirectory(finalDirectory);
            Directory.CreateDirectory(scratchDirectory);
            finalPath = Path.Combine(finalDirectory, SanitizeFileName(title) + ".mp4");
            scratchPath = Path.Combine(scratchDirectory, $"{job.Id}.mp4");
            job.Report(0.01, "Starting ffmpeg…");
            var duration = await ProbeDurationAsync(streamUri, job.CancellationToken);
            await RunFfmpegAsync(job, streamUri, scratchPath, duration);
            job.CancellationToken.ThrowIfCancellationRequested();
            File.Move(scratchPath, finalPath, overwrite: true);
            if (libraryItem.ItemId is int itemId)
                await libraryStore.UpdateStoragePathAsync(itemId, finalPath, CancellationToken.None);
            job.Complete("Download complete.");
        }
        catch (OperationCanceledException)
        {
            if (scratchPath is not null) TryDelete(scratchPath);
            await RemovePendingLibraryItemAsync(libraryItem);
        }
        catch (Exception exception)
        {
            if (scratchPath is not null) TryDelete(scratchPath);
            if (finalPath is not null && libraryItem.Created) TryDelete(finalPath);
            await RemovePendingLibraryItemAsync(libraryItem);
            job.Fail(exception.Message);
            activity.Notify("Download failed", $"{title}: {exception.Message}", PluginNotificationLevel.Error);
        }
        finally
        {
            if (acquired) ReleaseSlot();
        }
    }

    private async Task WaitForSlotAsync(int limit, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (concurrencyGate)
            {
                if (activeJobs < limit)
                {
                    activeJobs++;
                    return;
                }
            }
            await Task.Delay(200, cancellationToken);
        }
    }

    private void ReleaseSlot()
    {
        lock (concurrencyGate) activeJobs = Math.Max(0, activeJobs - 1);
    }

    private static async Task RunFfmpegAsync(IPluginJob job, Uri streamUri, string scratchPath, long? durationMicros)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 { "-hide_banner", "-loglevel", "error", "-y", "-i", streamUri.ToString(), "-c", "copy", "-bsf:a", "aac_adtstoasc", "-progress", "pipe:1", scratchPath })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg. Install ffmpeg and ensure it is on PATH.");
        using var registration = job.CancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });
        var stderr = process.StandardError.ReadToEndAsync();
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (durationMicros is > 0 && line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                long.TryParse(line[12..], out var micros))
                job.Report(Math.Clamp(micros / (double)durationMicros.Value, 0.01, 0.99), "Downloading media…");
        }
        await process.WaitForExitAsync(job.CancellationToken);
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"ffmpeg exited with code {process.ExitCode}." : error.Trim());
        if (!File.Exists(scratchPath)) throw new InvalidOperationException("ffmpeg did not produce an output file.");
    }

    private static async Task<long?> ProbeDurationAsync(Uri streamUri, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", streamUri.ToString() })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            using var registration = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            });
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 && double.TryParse(output.Trim(), out var seconds) && seconds > 0
                ? (long)(seconds * 1_000_000)
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string? NormalizeDirectory(string? path) => string.IsNullOrWhiteSpace(path)
        ? null
        : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "download" : name.Trim();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private async Task RemovePendingLibraryItemAsync(LibraryItemHandle handle)
    {
        if (handle.Created && handle.ItemId is int itemId)
            await libraryStore.DeleteAsync(itemId, CancellationToken.None);
    }
}

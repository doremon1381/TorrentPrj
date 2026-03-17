using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonoTorrent;
using MonoTorrent.Client;
using AppTorrentSettings = TorrentProject.Configuration.TorrentSettings;
using TorrentProject.Interfaces;
using TorrentProject.Models;
using TorrentFileInfo = TorrentProject.Models.TorrentFileInfo;

namespace TorrentProject.Services;

/// <summary>
/// MonoTorrent-based torrent service. Manages loading, per-file downloading, and cleanup.
/// </summary>
public sealed class TorrentService : ITorrentService, IDisposable
{
    #region Constants

    /// <summary>
    /// Polling interval for download progress checks.
    /// </summary>
    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromSeconds(3);

    #endregion

    #region Fields

    private readonly ILogger<TorrentService> _logger;
    private readonly AppTorrentSettings _settings;
    private ClientEngine? _engine;
    private TorrentManager? _manager;

    #endregion

    #region Constructor

    public TorrentService(
        IOptions<AppTorrentSettings> settings,
        ILogger<TorrentService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    #endregion

    #region Public Methods

    /// <inheritdoc />
    public async Task<TorrentMetadata> LoadTorrentAsync(
        string torrentPathOrMagnet, CancellationToken ct = default)
    {
        _engine = CreateEngine();

        var downloadDir = Path.GetFullPath(_settings.TempDownloadPath);
        Directory.CreateDirectory(downloadDir);

        _manager = await AddTorrentToEngineAsync(torrentPathOrMagnet, downloadDir);

        AttachManagerEventHandlers();

        if (_manager.HasMetadata is false)
        {
            await WaitForMagnetMetadataAsync(ct);
        }

        var metadata = BuildMetadata();

        _logger.LogInformation(
            "Torrent loaded: {Name} | {FileCount} files | {TotalSize:F2} MB",
            metadata.Name, metadata.Files.Count, metadata.TotalSize / 1024.0 / 1024.0);

        return metadata;
    }

    /// <inheritdoc />
    public async Task<string> DownloadFileAsync(
        int fileIndex, string downloadDirectory,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        EnsureLoaded();

        var targetFile = _manager!.Files[fileIndex];
        _logger.LogInformation(
            "Downloading file [{Index}/{Total}]: {Path} ({Size:F2} MB)",
            fileIndex + 1, _manager.Files.Count, targetFile.Path,
            targetFile.Length / 1024.0 / 1024.0);

        await SetSingleFilePriorityAsync(fileIndex);
        await _manager.StartAsync();
        await PollFileCompletionAsync(targetFile, progress, ct);
        await _manager.StopAsync();

        _logger.LogInformation("Download complete: {Path}", targetFile.FullPath);
        return targetFile.FullPath;
    }

    /// <inheritdoc />
    public ChannelReader<CompletedFileEvent> DownloadFilesConcurrentlyAsync(
        int maxConcurrent, CancellationToken ct = default)
    {
        EnsureLoaded();

        var channel = Channel.CreateUnbounded<CompletedFileEvent>();
        _ = RunConcurrentDownloadsAsync(maxConcurrent, channel.Writer, ct);

        return channel.Reader;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_manager is not null)
        {
            var stoppingTask = _manager.StopAsync();
            while (_manager.State != TorrentState.Stopped)
            {
                _logger.LogDebug("Waiting for torrent to stop... State: {State}", _manager.State);
                await Task.WhenAll(stoppingTask, Task.Delay(250));
            }
            await stoppingTask;
        }

        if (_engine is not null)
        {
            _logger.LogInformation("Torrent engine stopped");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _engine?.Dispose();
    }

    #endregion

    #region Private Methods — Engine Setup

    /// <summary>
    /// Create and configure the MonoTorrent client engine.
    /// </summary>
    private ClientEngine CreateEngine()
    {
        var engineSettings = new EngineSettingsBuilder
        {
            AllowPortForwarding = _settings.AllowPortForwarding,
            AutoSaveLoadDhtCache = _settings.AutoSaveLoadDhtCache,
            AutoSaveLoadFastResume = _settings.AutoSaveLoadFastResume,
            AutoSaveLoadMagnetLinkMetadata = true,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Any, 0) },
                { "ipv6", new IPEndPoint(IPAddress.IPv6Any, 0) }
            },
            DhtEndPoint = new IPEndPoint(IPAddress.Any, 0)
        };

        return new ClientEngine(engineSettings.ToSettings());
    }

    /// <summary>
    /// Add a torrent or magnet link to the engine and return its manager.
    /// </summary>
    private async Task<TorrentManager> AddTorrentToEngineAsync(
        string torrentPathOrMagnet, string downloadDir)
    {
        var torrentSettings = new TorrentSettingsBuilder
        {
            MaximumConnections = _settings.MaxConnections
        };

        if (MagnetLink.TryParse(torrentPathOrMagnet, out var magnetLink))
        {
            _logger.LogInformation("Loading magnet link: {Magnet}",
                torrentPathOrMagnet[..Math.Min(80, torrentPathOrMagnet.Length)]);
            return await _engine!.AddAsync(magnetLink, downloadDir, torrentSettings.ToSettings());
        }

        _logger.LogInformation("Loading torrent file: {Path}", torrentPathOrMagnet);
        return await _engine!.AddAsync(torrentPathOrMagnet, downloadDir, torrentSettings.ToSettings());
    }

    /// <summary>
    /// Attach logging handlers for state changes and peer discovery.
    /// </summary>
    private void AttachManagerEventHandlers()
    {
        _manager!.TorrentStateChanged += (_, e) =>
            _logger.LogInformation("Torrent state: {Old} → {New}", e.OldState, e.NewState);

        _manager.PeersFound += (_, e) =>
            _logger.LogDebug("{Type}: {NewPeers} new peers", e.GetType().Name, e.NewPeers);
    }

    /// <summary>
    /// Start the manager, wait for magnet metadata, then stop.
    /// </summary>
    private async Task WaitForMagnetMetadataAsync(CancellationToken ct)
    {
        _logger.LogInformation("Waiting for magnet metadata...");
        await _manager!.StartAsync();
        await _manager.WaitForMetadataAsync(ct);
        await _manager.StopAsync();
        _logger.LogInformation("Metadata received: {Name}", _manager.Torrent!.Name);
    }

    /// <summary>
    /// Build metadata from the loaded torrent's file list.
    /// </summary>
    private TorrentMetadata BuildMetadata()
    {
        var files = _manager!.Files
            .Select((f, i) => new TorrentFileInfo(
                Index: i,
                Path: f.Path,
                Size: f.Length,
                FullPath: f.FullPath))
            .ToList();

        return new TorrentMetadata(
            Name: _manager.Torrent?.Name ?? "Unknown",
            Files: files,
            TotalSize: _manager.Files.Sum(f => f.Length));
    }

    #endregion

    #region Private Methods — Single-File Download

    /// <summary>
    /// Throw if the torrent has not been loaded yet.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_manager is null || _engine is null)
            throw new InvalidOperationException("Torrent not loaded. Call LoadTorrentAsync first.");
    }

    /// <summary>
    /// Set one file to HIGH priority and all others to DoNotDownload.
    /// </summary>
    private async Task SetSingleFilePriorityAsync(int fileIndex)
    {
        foreach (var file in _manager!.Files)
        {
            await _manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
        }
        await _manager.SetFilePriorityAsync(_manager.Files[fileIndex], Priority.High);
    }

    /// <summary>
    /// Poll a single file until it reaches 100% completion.
    /// </summary>
    private async Task PollFileCompletionAsync(
        ITorrentManagerFile targetFile, IProgress<double>? progress, CancellationToken ct)
    {
        while (targetFile.BitField.PercentComplete < 100.0)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(targetFile.BitField.PercentComplete);

            var downSpeed = _engine!.TotalDownloadRate / 1024.0;
            var peers = await _manager!.GetPeersAsync();

            _logger.LogInformation(
                "  Progress: {Progress:F1}% | Speed: {Speed:F1} KB/s | Peers: {Peers}",
                targetFile.BitField.PercentComplete, downSpeed, peers.Count);

            await Task.Delay(ProgressPollInterval, ct);
        }

        progress?.Report(100.0);
    }

    #endregion

    #region Private Methods — Concurrent Download

    /// <summary>
    /// Core concurrent download loop. Sets up to N files to HIGH priority,
    /// polls progress, and writes completed files to the channel.
    /// </summary>
    private async Task RunConcurrentDownloadsAsync(
        int maxConcurrent, ChannelWriter<CompletedFileEvent> writer, CancellationToken ct)
    {
        try
        {
            var fileCount = _manager!.Files.Count;
            var effectiveConcurrent = Math.Min(maxConcurrent, fileCount);

            _logger.LogInformation(
                "Starting concurrent download: {Concurrent} slots for {Total} files",
                effectiveConcurrent, fileCount);

            await SetAllFilesDoNotDownloadAsync();

            var fileQueue = new Queue<int>(Enumerable.Range(0, fileCount));
            var activeFiles = new Dictionary<int, Stopwatch>();

            FillInitialBatch(fileQueue, activeFiles, effectiveConcurrent);
            await _manager.StartAsync();

            await PollConcurrentDownloadsAsync(fileQueue, activeFiles, fileCount, writer, ct);
            await _manager.StopAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during concurrent downloads");
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Set all files to DoNotDownload priority.
    /// </summary>
    private async Task SetAllFilesDoNotDownloadAsync()
    {
        foreach (var file in _manager!.Files)
        {
            await _manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
        }
    }

    /// <summary>
    /// Fill the initial batch of active downloads from the queue.
    /// </summary>
    private void FillInitialBatch(
        Queue<int> fileQueue, Dictionary<int, Stopwatch> activeFiles, int maxSlots)
    {
        while (activeFiles.Count < maxSlots && fileQueue.Count > 0)
        {
            var idx = fileQueue.Dequeue();
            ActivateFile(idx, activeFiles);
        }
    }

    /// <summary>
    /// Poll all active files, process completions, and refill slots from the queue.
    /// </summary>
    private async Task PollConcurrentDownloadsAsync(
        Queue<int> fileQueue, Dictionary<int, Stopwatch> activeFiles,
        int totalFiles, ChannelWriter<CompletedFileEvent> writer, CancellationToken ct)
    {
        while (activeFiles.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var completedIndices = FindCompletedFiles(activeFiles);

            foreach (var idx in completedIndices)
            {
                await ProcessCompletedDownloadAsync(idx, activeFiles, totalFiles, writer, ct);

                if (fileQueue.Count > 0)
                {
                    ActivateFile(fileQueue.Dequeue(), activeFiles);
                }
            }

            if (activeFiles.Count > 0)
            {
                LogConcurrentProgress(activeFiles);
            }

            await Task.Delay(ProgressPollInterval, ct);
        }
    }

    /// <summary>
    /// Find file indices that have reached 100% completion.
    /// </summary>
    private List<int> FindCompletedFiles(Dictionary<int, Stopwatch> activeFiles)
    {
        return activeFiles
            .Where(kv => _manager!.Files[kv.Key].BitField.PercentComplete >= 100.0)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Handle a completed file: stop tracking, set DoNotDownload, write to channel.
    /// </summary>
    private async Task ProcessCompletedDownloadAsync(
        int fileIndex, Dictionary<int, Stopwatch> activeFiles,
        int totalFiles, ChannelWriter<CompletedFileEvent> writer, CancellationToken ct)
    {
        var sw = activeFiles[fileIndex];
        sw.Stop();

        var file = _manager!.Files[fileIndex];
        await _manager.SetFilePriorityAsync(file, Priority.DoNotDownload);

        _logger.LogInformation(
            "✓ Download complete [{Index}/{Total}]: {Path} in {Time:F1}s",
            fileIndex + 1, totalFiles, file.Path, sw.Elapsed.TotalSeconds);

        await writer.WriteAsync(new CompletedFileEvent(
            FileIndex: fileIndex,
            LocalPath: file.FullPath,
            DownloadTime: sw.Elapsed), ct);

        activeFiles.Remove(fileIndex);
    }

    /// <summary>
    /// Activate a file for download: set priority to HIGH and start tracking.
    /// </summary>
    private void ActivateFile(int fileIndex, Dictionary<int, Stopwatch> activeFiles)
    {
        var file = _manager!.Files[fileIndex];
        _manager.SetFilePriorityAsync(file, Priority.High).GetAwaiter().GetResult();
        activeFiles[fileIndex] = Stopwatch.StartNew();

        _logger.LogInformation(
            "→ Queued for download [{Index}/{Total}]: {Path} ({Size:F2} MB)",
            fileIndex + 1, _manager.Files.Count, file.Path,
            file.Length / 1024.0 / 1024.0);
    }

    /// <summary>
    /// Log progress for all currently active concurrent downloads.
    /// </summary>
    private async void LogConcurrentProgress(Dictionary<int, Stopwatch> activeFiles)
    {
        var downSpeed = _engine!.TotalDownloadRate / 1024.0 / 1024.0;
        var peers = await _manager!.GetPeersAsync();

        foreach (var (idx, _) in activeFiles)
        {
            var file = _manager.Files[idx];
            _logger.LogInformation(
                "  [{Index}] {Name}: {Progress:F1}%",
                idx + 1, Path.GetFileName(file.Path), file.BitField.PercentComplete);
        }

        _logger.LogInformation(
            "  ↓ {Speed:F2} MB/s | Peers: {Peers} | Active: {Active}",
            downSpeed, peers.Count, activeFiles.Count);
    }

    #endregion
}

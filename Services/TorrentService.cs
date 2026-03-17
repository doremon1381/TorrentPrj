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
    private readonly ILogger<TorrentService> _logger;
    private readonly AppTorrentSettings _settings;
    private ClientEngine? _engine;
    private TorrentManager? _manager;

    public TorrentService(
        IOptions<AppTorrentSettings> settings,
        ILogger<TorrentService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TorrentMetadata> LoadTorrentAsync(
        string torrentPathOrMagnet, CancellationToken ct = default)
    {
        // Build engine settings
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

        _engine = new ClientEngine(engineSettings.ToSettings());

        // Resolve absolute download directory
        var downloadDir = Path.GetFullPath(_settings.TempDownloadPath);
        Directory.CreateDirectory(downloadDir);

        var torrentSettings = new TorrentSettingsBuilder
        {
            MaximumConnections = _settings.MaxConnections
        };

        // Load torrent or magnet
        if (MagnetLink.TryParse(torrentPathOrMagnet, out var magnetLink))
        {
            _logger.LogInformation("Loading magnet link: {Magnet}", torrentPathOrMagnet[..Math.Min(80, torrentPathOrMagnet.Length)]);
            _manager = await _engine.AddAsync(magnetLink, downloadDir, torrentSettings.ToSettings());
        }
        else
        {
            _logger.LogInformation("Loading torrent file: {Path}", torrentPathOrMagnet);
            _manager = await _engine.AddAsync(torrentPathOrMagnet, downloadDir, torrentSettings.ToSettings());
        }

        // Hook state change events
        _manager.TorrentStateChanged += (_, e) =>
            _logger.LogInformation("Torrent state: {Old} → {New}", e.OldState, e.NewState);

        _manager.PeersFound += (_, e) =>
            _logger.LogDebug("{Type}: {NewPeers} new peers", e.GetType().Name, e.NewPeers);

        // For magnet links, we need to start and wait for metadata
        if (_manager.HasMetadata is false)
        {
            _logger.LogInformation("Waiting for magnet metadata...");
            await _manager.StartAsync();
            await _manager.WaitForMetadataAsync(ct);
            await _manager.StopAsync();
            _logger.LogInformation("Metadata received: {Name}", _manager.Torrent!.Name);
        }

        // Build file list
        var files = _manager.Files
            .Select((f, i) => new TorrentFileInfo(
                Index: i,
                Path: f.Path,
                Size: f.Length,
                FullPath: f.FullPath))
            .ToList();

        var metadata = new TorrentMetadata(
            Name: _manager.Torrent?.Name ?? "Unknown",
            Files: files,
            TotalSize: _manager.Files.Sum(f => f.Length));

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
        if (_manager is null || _engine is null)
            throw new InvalidOperationException("Torrent not loaded. Call LoadTorrentAsync first.");

        var targetFile = _manager.Files[fileIndex];
        _logger.LogInformation(
            "Downloading file [{Index}/{Total}]: {Path} ({Size:F2} MB)",
            fileIndex + 1, _manager.Files.Count, targetFile.Path,
            targetFile.Length / 1024.0 / 1024.0);

        // Set file priorities: current = HIGH, others = DO_NOT_DOWNLOAD
        foreach (var file in _manager.Files)
        {
            await _manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
        }
        await _manager.SetFilePriorityAsync(targetFile, Priority.High);

        // Start downloading
        await _manager.StartAsync();

        // Poll until this file is complete
        while (targetFile.BitField.PercentComplete < 100.0)
        {
            ct.ThrowIfCancellationRequested();

            var fileProgress = targetFile.BitField.PercentComplete;
            progress?.Report(fileProgress);

            var downSpeed = _engine.TotalDownloadRate / 1024.0;
            var peers = await _manager.GetPeersAsync();

            _logger.LogInformation(
                "  Progress: {Progress:F1}% | Speed: {Speed:F1} KB/s | Peers: {Peers}",
                fileProgress, downSpeed, peers.Count);

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        progress?.Report(100.0);

        // Pause the manager (don't full-stop, so we can download next file)
        await _manager.StopAsync();

        _logger.LogInformation("Download complete: {Path}", targetFile.FullPath);
        return targetFile.FullPath;
    }

    /// <inheritdoc />
    public ChannelReader<CompletedFileEvent> DownloadFilesConcurrentlyAsync(
        int maxConcurrent, CancellationToken ct = default)
    {
        if (_manager is null || _engine is null)
            throw new InvalidOperationException("Torrent not loaded. Call LoadTorrentAsync first.");

        var channel = Channel.CreateUnbounded<CompletedFileEvent>();

        // Fire-and-forget the download loop — it writes to the channel
        _ = RunConcurrentDownloadsAsync(maxConcurrent, channel.Writer, ct);

        return channel.Reader;
    }

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

            // Set all files to DoNotDownload initially
            foreach (var file in _manager.Files)
            {
                await _manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
            }

            // Track active downloads and the file queue
            var fileQueue = new Queue<int>(Enumerable.Range(0, fileCount));
            var activeFiles = new Dictionary<int, Stopwatch>(); // fileIndex → stopwatch

            // Fill initial batch
            while (activeFiles.Count < effectiveConcurrent && fileQueue.Count > 0)
            {
                var idx = fileQueue.Dequeue();
                await ActivateFileAsync(idx, activeFiles);
            }

            // Start the manager
            await _manager.StartAsync();

            // Poll loop
            while (activeFiles.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                // Check for completed files
                var completedIndices = new List<int>();
                foreach (var (idx, sw) in activeFiles)
                {
                    if (_manager.Files[idx].BitField.PercentComplete >= 100.0)
                    {
                        completedIndices.Add(idx);
                    }
                }

                // Process completed files
                foreach (var idx in completedIndices)
                {
                    var sw = activeFiles[idx];
                    sw.Stop();

                    var file = _manager.Files[idx];
                    await _manager.SetFilePriorityAsync(file, Priority.DoNotDownload);

                    _logger.LogInformation(
                        "✓ Download complete [{Index}/{Total}]: {Path} in {Time:F1}s",
                        idx + 1, fileCount, file.Path, sw.Elapsed.TotalSeconds);

                    // Write to channel for upload processing
                    await writer.WriteAsync(new CompletedFileEvent(
                        FileIndex: idx,
                        LocalPath: file.FullPath,
                        DownloadTime: sw.Elapsed), ct);

                    activeFiles.Remove(idx);

                    // Activate next file from queue
                    if (fileQueue.Count > 0)
                    {
                        var nextIdx = fileQueue.Dequeue();
                        await ActivateFileAsync(nextIdx, activeFiles);
                    }
                }

                // Log progress for active files
                if (activeFiles.Count > 0)
                {
                    var downSpeed = _engine!.TotalDownloadRate / 1024.0 / 1024.0;
                    var peers = await _manager.GetPeersAsync();

                    foreach (var (idx, _) in activeFiles)
                    {
                        var file = _manager.Files[idx];
                        var pct = file.BitField.PercentComplete;
                        _logger.LogInformation(
                            "  [{Index}] {Name}: {Progress:F1}%",
                            idx + 1, Path.GetFileName(file.Path), pct);
                    }

                    _logger.LogInformation(
                        "  ↓ {Speed:F2} MB/s | Peers: {Peers} | Active: {Active} | Remaining: {Queue}",
                        downSpeed, peers.Count, activeFiles.Count, fileQueue.Count);
                }

                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }

            // Stop the manager after all files are done
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
    /// Activate a file for download: set priority to HIGH and start tracking.
    /// </summary>
    private async Task ActivateFileAsync(int fileIndex, Dictionary<int, Stopwatch> activeFiles)
    {
        var file = _manager!.Files[fileIndex];
        await _manager.SetFilePriorityAsync(file, Priority.High);
        activeFiles[fileIndex] = Stopwatch.StartNew();

        _logger.LogInformation(
            "→ Queued for download [{Index}/{Total}]: {Path} ({Size:F2} MB)",
            fileIndex + 1, _manager.Files.Count, file.Path,
            file.Length / 1024.0 / 1024.0);
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

    public void Dispose()
    {
        _engine?.Dispose();
    }
}

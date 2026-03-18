using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TorrentProject.Configuration;
using TorrentProject.Interfaces;
using TorrentProject.Models;

namespace TorrentProject.Workers;

/// <summary>
/// Background service that orchestrates the concurrent download pipeline:
/// Load torrent → download up to N files concurrently → upload each as it completes → delete → shutdown.
/// </summary>
public sealed class TorrentWorker(
    ITorrentService torrentService,
    IGoogleDriveService driveService,
    IOptions<TorrentSettings> torrentSettings,
    IOptions<GoogleDriveSettings> driveSettings,
    DownloadRequest downloadRequest,
    ILogger<TorrentWorker> _logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    #region Public Methods

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var results = new List<FileProcessResult>();

        try
        {
            var metadata = await LoadTorrentAsync(stoppingToken);
            var torrentFolderId = await CreateDriveFolderAsync(metadata, stoppingToken);
            var maxConcurrent = GetEffectiveConcurrency();

            _logger.LogInformation("Download mode: {Concurrent} concurrent file(s)", maxConcurrent);

            // Speed probe: analyze file speeds, then download fastest first
            var probeDuration = TimeSpan.FromSeconds(
                torrentSettings.Value.SpeedProbeDurationSeconds);
            var fileOrder = await torrentService.ProbeFileSpeedsAsync(probeDuration, stoppingToken);

            var completedReader = torrentService.DownloadFilesConcurrentlyAsync(
                maxConcurrent, fileOrder, stoppingToken);

            await ProcessCompletedFilesAsync(
                completedReader, metadata, torrentFolderId, results, stoppingToken);

            LogFinalSummary(metadata, results);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in torrent worker");
        }
        finally
        {
            await torrentService.StopAsync();
            lifetime.StopApplication();
        }
    }

    #endregion

    #region Private Methods — Pipeline Stages

    /// <summary>
    /// Load the torrent metadata and log a summary of its contents.
    /// </summary>
    private async Task<TorrentMetadata> LoadTorrentAsync(CancellationToken ct)
    {
        _logger.LogInformation("═══ TorrentProject – Starting ═══");

        var metadata = await torrentService.LoadTorrentAsync(
            downloadRequest.InputValue, ct);

        LogTorrentSummary(metadata);
        return metadata;
    }

    /// <summary>
    /// Create a Drive folder for this torrent if a target folder ID is configured.
    /// </summary>
    private async Task<string?> CreateDriveFolderAsync(
        TorrentMetadata metadata, CancellationToken ct)
    {
        var targetFolderId = downloadRequest.DriveFolderId
            ?? driveSettings.Value.TargetFolderId;

        if (string.IsNullOrEmpty(targetFolderId))
            return null;

        var torrentFolderId = await driveService.CreateFolderAsync(
            metadata.Name, targetFolderId, ct);

        _logger.LogInformation("Created Drive folder: {Name} → {Id}", metadata.Name, torrentFolderId);
        return torrentFolderId;
    }

    /// <summary>
    /// Determine the effective concurrency from CLI override or appsettings default.
    /// </summary>
    private int GetEffectiveConcurrency()
    {
        return downloadRequest.MaxConcurrentFiles
            ?? torrentSettings.Value.MaxConcurrentFiles;
    }

    /// <summary>
    /// Read completed downloads from the channel, upload each, and delete local files.
    /// </summary>
    private async Task ProcessCompletedFilesAsync(
        System.Threading.Channels.ChannelReader<CompletedFileEvent> completedReader,
        TorrentMetadata metadata, string? torrentFolderId,
        List<FileProcessResult> results, CancellationToken ct)
    {
        await foreach (var completed in completedReader.ReadAllAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var fileInfo = metadata.Files[completed.FileIndex];
            _logger.LogInformation(
                "═══ Uploading [{Index}/{Total}]: {Path} ═══",
                completed.FileIndex + 1, metadata.Files.Count, fileInfo.Path);

            var result = await UploadAndCleanupAsync(
                completed, fileInfo, torrentFolderId, ct);

            results.Add(result);

            _logger.LogInformation(
                "✓ Complete: {Name} | DL: {DlTime:F1}s | UL: {UlTime:F1}s | Drive: {DriveId}",
                result.FileName, result.DownloadTime.TotalSeconds,
                result.UploadTime.TotalSeconds, result.DriveFileId);
        }
    }

    #endregion

    #region Private Methods — File Processing

    /// <summary>
    /// Upload a completed file to Drive and delete the local copy.
    /// </summary>
    private async Task<FileProcessResult> UploadAndCleanupAsync(
        CompletedFileEvent completed,
        TorrentFileInfo fileInfo,
        string? targetFolderId,
        CancellationToken ct)
    {
        var ulStopwatch = Stopwatch.StartNew();

        var driveFileId = await driveService.UploadFileAsync(
            completed.LocalPath, targetFolderId, ct: ct);

        ulStopwatch.Stop();

        DeleteLocalFile(completed.LocalPath);

        return new FileProcessResult(
            FileName: fileInfo.Path,
            FileSize: fileInfo.Size,
            DriveFileId: driveFileId,
            DownloadTime: completed.DownloadTime,
            UploadTime: ulStopwatch.Elapsed);
    }

    /// <summary>
    /// Safely delete a temporary local file after successful upload.
    /// </summary>
    private void DeleteLocalFile(string localPath)
    {
        try
        {
            if (!File.Exists(localPath)) return;

            var fileSize = new FileInfo(localPath).Length;
            File.Delete(localPath);
            _logger.LogInformation(
                "Deleted temp file: {Path} ({Size:F2} MB freed)",
                localPath, fileSize / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp file: {Path}", localPath);
        }
    }

    #endregion

    #region Private Methods — Logging

    /// <summary>
    /// Log a summary of the torrent's file list.
    /// </summary>
    private void LogTorrentSummary(TorrentMetadata metadata)
    {
        _logger.LogInformation("Torrent: {Name}", metadata.Name);
        _logger.LogInformation("Files:   {Count}", metadata.Files.Count);
        _logger.LogInformation("Total:   {Size:F2} MB", metadata.TotalSize / 1024.0 / 1024.0);
        _logger.LogInformation("Largest: {Size:F2} MB (max disk per slot)",
            metadata.Files.Max(f => f.Size) / 1024.0 / 1024.0);
        _logger.LogInformation("─────────────────────────────────────────");

        foreach (var file in metadata.Files)
        {
            _logger.LogInformation(
                "  [{Index}] {Path} ({Size:F2} MB)",
                file.Index, file.Path, file.Size / 1024.0 / 1024.0);
        }

        _logger.LogInformation("─────────────────────────────────────────");
    }

    /// <summary>
    /// Log a final summary of all processed files.
    /// </summary>
    private void LogFinalSummary(TorrentMetadata metadata, List<FileProcessResult> results)
    {
        _logger.LogInformation("═══ TorrentProject – Complete ═══");
        _logger.LogInformation("Torrent:        {Name}", metadata.Name);
        _logger.LogInformation("Files processed: {Count}/{Total}",
            results.Count, metadata.Files.Count);

        var totalDlTime = TimeSpan.FromTicks(results.Sum(r => r.DownloadTime.Ticks));
        var totalUlTime = TimeSpan.FromTicks(results.Sum(r => r.UploadTime.Ticks));
        var totalSize = results.Sum(r => r.FileSize);

        _logger.LogInformation("Total size:     {Size:F2} MB", totalSize / 1024.0 / 1024.0);
        _logger.LogInformation("Total download: {Time}", totalDlTime);
        _logger.LogInformation("Total upload:   {Time}", totalUlTime);
        _logger.LogInformation("Wall time:      {Time} (downloads were concurrent)",
            TimeSpan.FromTicks(Math.Max(totalDlTime.Ticks, totalUlTime.Ticks)));
    }

    #endregion
}

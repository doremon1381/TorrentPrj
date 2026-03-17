using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TorrentProject.Configuration;
using TorrentProject.Interfaces;
using TorrentProject.Models;

namespace TorrentProject.Workers;

/// <summary>
/// Background service that orchestrates the per-file pipeline:
/// Load torrent → for each file: download → upload → delete → next → shutdown.
/// </summary>
public sealed class TorrentWorker(
    ITorrentService torrentService,
    IGoogleDriveService driveService,
    IOptions<TorrentSettings> torrentSettings,
    IOptions<GoogleDriveSettings> driveSettings,
    DownloadRequest downloadRequest,
    ILogger<TorrentWorker> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var results = new List<FileProcessResult>();

        try
        {
            // 1. Load torrent metadata
            logger.LogInformation("═══ TorrentProject – Starting ═══");
            var metadata = await torrentService.LoadTorrentAsync(
                downloadRequest.InputValue, stoppingToken);

            LogTorrentSummary(metadata);

            // 2. Create a Drive folder for this torrent (optional)
            var targetFolderId = downloadRequest.DriveFolderId
                ?? driveSettings.Value.TargetFolderId;

            string? torrentFolderId = null;
            if (!string.IsNullOrEmpty(targetFolderId))
            {
                torrentFolderId = await driveService.CreateFolderAsync(
                    metadata.Name, targetFolderId, stoppingToken);
                logger.LogInformation("Created Drive folder: {Name} → {Id}", metadata.Name, torrentFolderId);
            }

            // 3. Per-file loop: download → upload → delete
            for (var i = 0; i < metadata.Files.Count; i++)
            {
                stoppingToken.ThrowIfCancellationRequested();

                var file = metadata.Files[i];
                logger.LogInformation(
                    "═══ Processing file [{Index}/{Total}]: {Path} ({Size:F2} MB) ═══",
                    i + 1, metadata.Files.Count, file.Path,
                    file.Size / 1024.0 / 1024.0);

                var result = await ProcessSingleFileAsync(
                    i, file, torrentFolderId, stoppingToken);

                results.Add(result);

                logger.LogInformation(
                    "✓ File complete: {Name} | Download: {DlTime:F1}s | Upload: {UlTime:F1}s | Drive ID: {DriveId}",
                    result.FileName, result.DownloadTime.TotalSeconds,
                    result.UploadTime.TotalSeconds, result.DriveFileId);
            }

            // 4. Summary
            LogFinalSummary(metadata, results);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Operation was cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error in torrent worker");
        }
        finally
        {
            await torrentService.StopAsync();
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// Process a single file: download → upload → delete local copy.
    /// </summary>
    private async Task<FileProcessResult> ProcessSingleFileAsync(
        int fileIndex,
        Models.TorrentFileInfo fileInfo,
        string? targetFolderId,
        CancellationToken ct)
    {
        var downloadDir = torrentSettings.Value.TempDownloadPath;

        // Download
        var dlStopwatch = Stopwatch.StartNew();
        var downloadProgress = new Progress<double>(pct =>
        {
            // Progress is already logged inside TorrentService
        });

        var localPath = await torrentService.DownloadFileAsync(
            fileIndex, downloadDir, downloadProgress, ct);
        dlStopwatch.Stop();

        // Upload
        var ulStopwatch = Stopwatch.StartNew();
        var uploadProgress = new Progress<long>(bytes =>
        {
            // Progress is already logged inside GoogleDriveService
        });

        var driveFileId = await driveService.UploadFileAsync(
            localPath, targetFolderId, uploadProgress, ct);
        ulStopwatch.Stop();

        // Delete local file
        try
        {
            if (File.Exists(localPath))
            {
                var fileSize = new FileInfo(localPath).Length;
                File.Delete(localPath);
                logger.LogInformation(
                    "Deleted temp file: {Path} ({Size:F2} MB freed)",
                    localPath, fileSize / 1024.0 / 1024.0);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete temp file: {Path}", localPath);
        }

        return new FileProcessResult(
            FileName: fileInfo.Path,
            FileSize: fileInfo.Size,
            DriveFileId: driveFileId,
            DownloadTime: dlStopwatch.Elapsed,
            UploadTime: ulStopwatch.Elapsed);
    }

    private void LogTorrentSummary(TorrentMetadata metadata)
    {
        logger.LogInformation("Torrent: {Name}", metadata.Name);
        logger.LogInformation("Files:   {Count}", metadata.Files.Count);
        logger.LogInformation("Total:   {Size:F2} MB", metadata.TotalSize / 1024.0 / 1024.0);
        logger.LogInformation("Largest: {Size:F2} MB (max disk usage)",
            metadata.Files.Max(f => f.Size) / 1024.0 / 1024.0);
        logger.LogInformation("─────────────────────────────────────────");

        foreach (var file in metadata.Files)
        {
            logger.LogInformation(
                "  [{Index}] {Path} ({Size:F2} MB)",
                file.Index, file.Path, file.Size / 1024.0 / 1024.0);
        }

        logger.LogInformation("─────────────────────────────────────────");
    }

    private void LogFinalSummary(TorrentMetadata metadata, List<FileProcessResult> results)
    {
        logger.LogInformation("═══ TorrentProject – Complete ═══");
        logger.LogInformation("Torrent:        {Name}", metadata.Name);
        logger.LogInformation("Files processed: {Count}/{Total}",
            results.Count, metadata.Files.Count);

        var totalDlTime = TimeSpan.FromTicks(results.Sum(r => r.DownloadTime.Ticks));
        var totalUlTime = TimeSpan.FromTicks(results.Sum(r => r.UploadTime.Ticks));
        var totalSize = results.Sum(r => r.FileSize);

        logger.LogInformation("Total size:     {Size:F2} MB", totalSize / 1024.0 / 1024.0);
        logger.LogInformation("Total download: {Time}", totalDlTime);
        logger.LogInformation("Total upload:   {Time}", totalUlTime);
        logger.LogInformation("Total time:     {Time}", totalDlTime + totalUlTime);
    }
}

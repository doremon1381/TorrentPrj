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

            // 3. Determine concurrency
            var maxConcurrent = downloadRequest.MaxConcurrentFiles
                ?? torrentSettings.Value.MaxConcurrentFiles;

            logger.LogInformation(
                "Download mode: {Concurrent} concurrent file(s)",
                maxConcurrent);

            // 4. Start concurrent downloads — reads completed files from channel
            var completedReader = torrentService.DownloadFilesConcurrentlyAsync(
                maxConcurrent, stoppingToken);

            // 5. Process each completed file: upload → delete
            await foreach (var completed in completedReader.ReadAllAsync(stoppingToken))
            {
                stoppingToken.ThrowIfCancellationRequested();

                var fileInfo = metadata.Files[completed.FileIndex];
                logger.LogInformation(
                    "═══ Uploading [{Index}/{Total}]: {Path} ═══",
                    completed.FileIndex + 1, metadata.Files.Count, fileInfo.Path);

                var result = await UploadAndCleanupAsync(
                    completed, fileInfo, torrentFolderId, stoppingToken);

                results.Add(result);

                logger.LogInformation(
                    "✓ Complete: {Name} | DL: {DlTime:F1}s | UL: {UlTime:F1}s | Drive: {DriveId}",
                    result.FileName, result.DownloadTime.TotalSeconds,
                    result.UploadTime.TotalSeconds, result.DriveFileId);
            }

            // 6. Summary
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
    /// Upload a completed file to Drive and delete the local copy.
    /// </summary>
    private async Task<FileProcessResult> UploadAndCleanupAsync(
        CompletedFileEvent completed,
        TorrentFileInfo fileInfo,
        string? targetFolderId,
        CancellationToken ct)
    {
        // Upload
        var ulStopwatch = Stopwatch.StartNew();
        var uploadProgress = new Progress<long>(_ => { });

        var driveFileId = await driveService.UploadFileAsync(
            completed.LocalPath, targetFolderId, uploadProgress, ct);
        ulStopwatch.Stop();

        // Delete local file
        try
        {
            if (File.Exists(completed.LocalPath))
            {
                var fileSize = new FileInfo(completed.LocalPath).Length;
                File.Delete(completed.LocalPath);
                logger.LogInformation(
                    "Deleted temp file: {Path} ({Size:F2} MB freed)",
                    completed.LocalPath, fileSize / 1024.0 / 1024.0);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete temp file: {Path}", completed.LocalPath);
        }

        return new FileProcessResult(
            FileName: fileInfo.Path,
            FileSize: fileInfo.Size,
            DriveFileId: driveFileId,
            DownloadTime: completed.DownloadTime,
            UploadTime: ulStopwatch.Elapsed);
    }

    private void LogTorrentSummary(TorrentMetadata metadata)
    {
        logger.LogInformation("Torrent: {Name}", metadata.Name);
        logger.LogInformation("Files:   {Count}", metadata.Files.Count);
        logger.LogInformation("Total:   {Size:F2} MB", metadata.TotalSize / 1024.0 / 1024.0);
        logger.LogInformation("Largest: {Size:F2} MB (max disk per slot)",
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
        logger.LogInformation("Wall time:      {Time} (downloads were concurrent)",
            TimeSpan.FromTicks(Math.Max(totalDlTime.Ticks, totalUlTime.Ticks)));
    }
}

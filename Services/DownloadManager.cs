using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TorrentProject.Configuration;
using TorrentProject.Interfaces;
using TorrentProject.Models;

namespace TorrentProject.Services;

/// <summary>
/// Orchestrates multiple torrent jobs with global concurrency control.
/// Each job gets its own TorrentService instance.
/// </summary>
public sealed class DownloadManager
{
    #region Fields

    private readonly IServiceProvider _serviceProvider;
    private readonly IGoogleDriveService _driveService;
    private readonly TorrentSettings _torrentSettings;
    private readonly GoogleDriveSettings _driveSettings;
    private readonly ILogger<DownloadManager> _logger;
    private readonly ConcurrentDictionary<int, TorrentJob> _jobs = new();
    private readonly SemaphoreSlim _globalSlots;

    #endregion

    #region Constructor

    public DownloadManager(
        IServiceProvider serviceProvider,
        IGoogleDriveService driveService,
        IOptions<TorrentSettings> torrentSettings,
        IOptions<GoogleDriveSettings> driveSettings,
        ILogger<DownloadManager> logger)
    {
        _serviceProvider = serviceProvider;
        _driveService = driveService;
        _torrentSettings = torrentSettings.Value;
        _driveSettings = driveSettings.Value;
        _logger = logger;
        _globalSlots = new SemaphoreSlim(_torrentSettings.MaxConcurrentFiles);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Add a new torrent job and start processing it in the background.
    /// </summary>
    public TorrentJob AddJob(string inputValue)
    {
        var job = new TorrentJob { InputValue = inputValue };
        _jobs[job.Id] = job;

        _ = ProcessJobAsync(job);

        return job;
    }

    /// <summary>
    /// Get all tracked jobs.
    /// </summary>
    public IReadOnlyCollection<TorrentJob> GetAllJobs()
    {
        return _jobs.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Get a specific job by ID.
    /// </summary>
    public TorrentJob? GetJob(int jobId)
    {
        return _jobs.GetValueOrDefault(jobId);
    }

    /// <summary>
    /// Pause a running job.
    /// </summary>
    public bool Pause(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.State is not TorrentJobState.Downloading and not TorrentJobState.Probing) return false;

        job.Cts.Cancel();
        job.State = TorrentJobState.Paused;
        _logger.LogDebug("[{Id}] {Name} → Paused", job.Id, job.Name);
        return true;
    }

    /// <summary>
    /// Resume a paused job by creating a new CTS and restarting.
    /// </summary>
    public bool Resume(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.State is not TorrentJobState.Paused and not TorrentJobState.Stopped) return false;

        // Create new job with same metadata but fresh CTS
        var resumedJob = new TorrentJob { InputValue = job.InputValue };
        resumedJob.Name = job.Name;
        resumedJob.Metadata = job.Metadata;
        resumedJob.FileOrder = job.FileOrder;
        resumedJob.DriveFolderId = job.DriveFolderId;
        foreach (var result in job.Results)
            resumedJob.Results.Add(result);

        // Replace the old job
        _jobs[jobId] = resumedJob;

        // Restart from where it left off
        _ = ProcessJobAsync(resumedJob, skipCompleted: true);

        _logger.LogDebug("[{Id}] {Name} → Resumed", jobId, resumedJob.Name);
        return true;
    }

    /// <summary>
    /// Stop a job but keep it in the list for possible resume.
    /// </summary>
    public bool Stop(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.State is TorrentJobState.Done or TorrentJobState.Failed or TorrentJobState.Stopped) return false;

        job.Cts.Cancel();
        job.State = TorrentJobState.Stopped;
        _logger.LogDebug("[{Id}] {Name} → Stopped", job.Id, job.Name);
        return true;
    }

    /// <summary>
    /// Delete a stopped, done, or failed job from the list.
    /// </summary>
    public bool Delete(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.State is not TorrentJobState.Stopped
            and not TorrentJobState.Done
            and not TorrentJobState.Failed) return false;

        _jobs.TryRemove(jobId, out _);
        _logger.LogDebug("[{Id}] {Name} → Deleted", job.Id, job.Name);
        return true;
    }

    /// <summary>
    /// Queue specific skipped files for download in a job.
    /// Removes them from skipped list and starts processing.
    /// </summary>
    public bool QueueFiles(int jobId, int[] fileIndices)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.State is not TorrentJobState.Stopped
            and not TorrentJobState.Done
            and not TorrentJobState.Failed) return false;
        if (job.Metadata is null || job.FileOrder is null) return false;

        // Remove from skipped list
        foreach (var idx in fileIndices)
        {
            job.SkippedFileIndices.Remove(idx);
        }

        // Create a new job with only the requested files
        var resumedJob = new TorrentJob { InputValue = job.InputValue };
        resumedJob.Name = job.Name;
        resumedJob.Metadata = job.Metadata;
        resumedJob.FileOrder = fileIndices; // Only download these specific files
        resumedJob.DriveFolderId = job.DriveFolderId;
        foreach (var result in job.Results)
            resumedJob.Results.Add(result);
        // Copy over the skipped list
        foreach (var idx in job.SkippedFileIndices)
            resumedJob.SkippedFileIndices.Add(idx);

        _jobs[jobId] = resumedJob;
        _ = ProcessJobAsync(resumedJob, skipCompleted: true);

        _logger.LogDebug(
            "[{Id}] Queued {Count} additional files for download",
            jobId, fileIndices.Length);
        return true;
    }

    /// <summary>
    /// Gracefully shut down all running jobs.
    /// </summary>
    public async Task ShutdownAsync()
    {
        foreach (var job in _jobs.Values)
        {
            job.Cts.Cancel();
        }

        // Wait a moment for cancellations to propagate
        await Task.Delay(1000);

        _logger.LogDebug("All jobs shut down");
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Process a single torrent job: load → probe → download → upload.
    /// </summary>
    private async Task ProcessJobAsync(TorrentJob job, bool skipCompleted = false)
    {
        ITorrentService? torrentService = null;
        var filesToDelete = new List<string>();

        try
        {
            var loggerFactory = (ILoggerFactory)_serviceProvider.GetService(typeof(ILoggerFactory))!;
            var settingsOptions = (IOptions<TorrentSettings>)_serviceProvider
                .GetService(typeof(IOptions<TorrentSettings>))!;

            torrentService = new TorrentService(settingsOptions,
                loggerFactory.CreateLogger<TorrentService>());

            // 1. Load metadata (if not already loaded from a resume)
            if (job.Metadata is null)
            {
                job.State = TorrentJobState.Probing;
                var metadata = await torrentService.LoadTorrentAsync(
                    job.InputValue, job.Cts.Token);
                job.Name = metadata.Name;
                job.Metadata = metadata;

                // Create Drive folder
                var targetFolderId = _driveSettings.TargetFolderId;
                if (!string.IsNullOrEmpty(targetFolderId))
                {
                    job.DriveFolderId = await _driveService.CreateFolderAsync(
                        metadata.Name, targetFolderId, job.Cts.Token);
                }
            }

            // 2. Speed probe (if not already probed)
            if (job.FileOrder is null)
            {
                var probeDuration = TimeSpan.FromSeconds(_torrentSettings.SpeedProbeDurationSeconds);
                job.FileOrder = await torrentService.ProbeFileSpeedsAsync(
                    probeDuration, job.Cts.Token);
            }

            // 3. Apply file filter (skip Extra folders, non-video, deeply nested)
            if (job.SkippedFileIndices.Count == 0 && !skipCompleted)
            {
                FilterFileOrder(job);
            }

            // 4. Filter out already-completed and skipped files (for resume)
            var fileOrder = job.FileOrder;
            if (job.SkippedFileIndices.Count > 0)
            {
                fileOrder = fileOrder!.Where(i => !job.SkippedFileIndices.Contains(i)).ToArray();
            }
            if (skipCompleted && job.Results.Count > 0)
            {
                var completedIndices = job.Results.Select(r =>
                    job.Metadata!.Files.ToList().FindIndex(f => f.Path == r.FileName)).ToHashSet();
                fileOrder = fileOrder!.Where(i => !completedIndices.Contains(i)).ToArray();
            }

            // 5. Download concurrently
            job.State = TorrentJobState.Downloading;
            var reader = torrentService.DownloadFilesConcurrentlyAsync(
                _torrentSettings.MaxConcurrentFiles, fileOrder, job.Cts.Token);

            // 6. Process completed files: copy → upload → delete copy
            await foreach (var completed in reader.ReadAllAsync(job.Cts.Token))
            {
                var fileInfo = job.Metadata!.Files[completed.FileIndex];

                // Copy to a temp file (bypasses MonoTorrent's file lock)
                var tempCopy = completed.LocalPath + ".uploading";
                await CopyFileWithShareAsync(completed.LocalPath, tempCopy, job.Cts.Token);

                // Upload from the unlocked copy, using original filename for Drive
                var ulStopwatch = Stopwatch.StartNew();
                var driveFileId = await _driveService.UploadFileAsync(
                    tempCopy, job.DriveFolderId,
                    fileName: Path.GetFileName(completed.LocalPath),
                    ct: job.Cts.Token);
                ulStopwatch.Stop();

                // Delete the copy (we exclusively own it)
                TryDeleteFile(tempCopy);

                // Track original for cleanup after engine stops
                filesToDelete.Add(completed.LocalPath);

                var result = new FileProcessResult(
                    FileName: fileInfo.Path,
                    FileSize: fileInfo.Size,
                    DriveFileId: driveFileId,
                    DownloadTime: completed.DownloadTime,
                    UploadTime: ulStopwatch.Elapsed);

                job.Results.Add(result);

                _logger.LogDebug(
                    "[{Id}] ✓ {Name}: {Completed}/{Total} files done",
                    job.Id, job.Name, job.CompletedFiles, job.TotalFiles);
            }

            job.State = TorrentJobState.Done;
            _logger.LogDebug("[{Id}] {Name} → All files processed!", job.Id, job.Name);
        }
        catch (OperationCanceledException)
        {
            // State already set by caller (Paused or Stopped)
            if (job.State is not TorrentJobState.Paused and not TorrentJobState.Stopped)
                job.State = TorrentJobState.Stopped;
        }
        catch (Exception ex)
        {
            job.State = TorrentJobState.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "[{Id}] {Name} → Failed", job.Id, job.Name);
        }
        finally
        {
            // Stop engine first to release all file handles
            if (torrentService is not null)
            {
                await torrentService.StopAsync();
                (torrentService as IDisposable)?.Dispose();
            }

            // Now delete local files (handles are released)
            foreach (var path in filesToDelete)
            {
                await DeleteLocalFileAsync(path);
                _logger.LogInformation("Deleted local file: {Path}", path);
            }
        }
    }

    /// <summary>
    /// Safely delete a temporary local file with retry for locked files.
    /// </summary>
    private async Task DeleteLocalFileAsync(string localPath)
    {
        if (!File.Exists(localPath)) return;

        const int maxRetries = 5;
        const int delayMs = 2000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Delete(localPath);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                _logger.LogDebug(
                    "File locked, retry {Attempt}/{Max} in {Delay}ms: {Path}",
                    attempt, maxRetries, delayMs, localPath);
                await Task.Delay(delayMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temp file: {Path}", localPath);
                return;
            }
        }
    }

    /// <summary>
    /// Copy a file using FileShare.ReadWrite to bypass locks from other processes.
    /// </summary>
    private static async Task CopyFileWithShareAsync(
        string sourcePath, string destPath, CancellationToken ct)
    {
        const int bufferSize = 81920; // 80 KB

        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, true);
        await using var dest = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true);

        await source.CopyToAsync(dest, ct);
    }

    /// <summary>
    /// Try to delete a file, swallowing any errors.
    /// </summary>
    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp copy: {Path}", path);
        }
    }

    /// <summary>
    /// Video file extensions considered for auto-download.
    /// </summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts"
    };

    /// <summary>
    /// Filter file order: skip Extra/EXTRA folders, only keep videos in first-level folder.
    /// Skipped file indices are stored in job.SkippedFileIndices.
    /// </summary>
    private void FilterFileOrder(TorrentJob job)
    {
        if (job.Metadata is null || job.FileOrder is null) return;

        foreach (var fileIndex in job.FileOrder)
        {
            var file = job.Metadata.Files[fileIndex];
            var normalizedPath = file.Path.Replace('\\', '/');
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Skip files in "Extra" or "EXTRA" folders (case-insensitive)
            if (IsInExtraFolder(segments))
            {
                job.SkippedFileIndices.Add(fileIndex);
                _logger.LogDebug("[{Id}] Skipped (Extra folder): {Path}", job.Id, file.Path);
                continue;
            }

            // Only auto-download files in the first-level folder
            // First-level = file is directly in root, or one folder deep
            // segments: ["TorrentName", "file.mkv"] = first level
            // segments: ["TorrentName", "Season 1", "file.mkv"] = nested, skip
            if (!IsFirstLevelFile(segments))
            {
                job.SkippedFileIndices.Add(fileIndex);
                _logger.LogDebug("[{Id}] Skipped (nested): {Path}", job.Id, file.Path);
                continue;
            }
        }

        var skippedCount = job.SkippedFileIndices.Count;
        if (skippedCount > 0)
        {
            _logger.LogDebug(
                "[{Id}] {Name}: {Skipped} files skipped, {Active} files queued for download",
                job.Id, job.Name, skippedCount, job.ActiveFileCount);
        }
    }

    /// <summary>
    /// Check if any path segment is "Extra" (case-insensitive).
    /// </summary>
    private static bool IsInExtraFolder(string[] segments)
    {
        // Check all segments except the last one (filename)
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("Extra", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("Extras", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("Featurettes", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A file is "first level" if its path has at most 2 segments:
    /// [TorrentName]/[file] or just [file].
    /// </summary>
    private static bool IsFirstLevelFile(string[] segments)
    {
        return segments.Length <= 2;
    }

    #endregion
}

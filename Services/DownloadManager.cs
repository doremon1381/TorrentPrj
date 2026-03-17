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
        _logger.LogInformation("[{Id}] {Name} → Paused", job.Id, job.Name);
        return true;
    }

    /// <summary>
    /// Resume a paused job by creating a new CTS and restarting.
    /// </summary>
    public bool Resume(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.State != TorrentJobState.Paused) return false;

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

        _logger.LogInformation("[{Id}] {Name} → Resumed", jobId, resumedJob.Name);
        return true;
    }

    /// <summary>
    /// Stop and remove a job.
    /// </summary>
    public bool Stop(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;

        job.Cts.Cancel();
        _jobs.TryRemove(jobId, out _);
        _logger.LogInformation("[{Id}] {Name} → Stopped and removed", job.Id, job.Name);
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

        _logger.LogInformation("All jobs shut down");
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Process a single torrent job: load → probe → download → upload.
    /// </summary>
    private async Task ProcessJobAsync(TorrentJob job, bool skipCompleted = false)
    {
        ITorrentService? torrentService = null;

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

            // 3. Filter out already-completed files (for resume)
            var fileOrder = job.FileOrder;
            if (skipCompleted && job.Results.Count > 0)
            {
                var completedIndices = job.Results.Select(r =>
                    job.Metadata!.Files.ToList().FindIndex(f => f.Path == r.FileName)).ToHashSet();
                fileOrder = fileOrder!.Where(i => !completedIndices.Contains(i)).ToArray();
            }

            // 4. Download concurrently
            job.State = TorrentJobState.Downloading;
            var reader = torrentService.DownloadFilesConcurrentlyAsync(
                _torrentSettings.MaxConcurrentFiles, fileOrder, job.Cts.Token);

            // 5. Process completed files: upload → delete
            await foreach (var completed in reader.ReadAllAsync(job.Cts.Token))
            {
                var fileInfo = job.Metadata!.Files[completed.FileIndex];

                // Upload
                var ulStopwatch = Stopwatch.StartNew();
                var driveFileId = await _driveService.UploadFileAsync(
                    completed.LocalPath, job.DriveFolderId, ct: job.Cts.Token);
                ulStopwatch.Stop();

                // Delete local file
                DeleteLocalFile(completed.LocalPath);

                var result = new FileProcessResult(
                    FileName: fileInfo.Path,
                    FileSize: fileInfo.Size,
                    DriveFileId: driveFileId,
                    DownloadTime: completed.DownloadTime,
                    UploadTime: ulStopwatch.Elapsed);

                job.Results.Add(result);

                _logger.LogInformation(
                    "[{Id}] ✓ {Name}: {Completed}/{Total} files done",
                    job.Id, job.Name, job.CompletedFiles, job.TotalFiles);
            }

            job.State = TorrentJobState.Done;
            _logger.LogInformation("[{Id}] {Name} → All files processed!", job.Id, job.Name);
        }
        catch (OperationCanceledException)
        {
            // Pause or stop — state already set by caller
            if (job.State != TorrentJobState.Paused)
                job.State = TorrentJobState.Paused;
        }
        catch (Exception ex)
        {
            job.State = TorrentJobState.Failed;
            job.Error = ex.Message;
            _logger.LogError(ex, "[{Id}] {Name} → Failed", job.Id, job.Name);
        }
        finally
        {
            if (torrentService is not null)
            {
                await torrentService.StopAsync();
                (torrentService as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Safely delete a temporary local file.
    /// </summary>
    private void DeleteLocalFile(string localPath)
    {
        try
        {
            if (!File.Exists(localPath)) return;
            File.Delete(localPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp file: {Path}", localPath);
        }
    }

    #endregion
}

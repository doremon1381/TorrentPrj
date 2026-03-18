using Microsoft.Extensions.Hosting;
using TorrentProject.Models;

namespace TorrentProject.Services;

/// <summary>
/// Interactive command-line handler for daemon mode.
/// Reads stdin commands and dispatches to DownloadManager.
/// </summary>
public sealed class CommandHandler(
    DownloadManager downloadManager,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    #region Public Methods

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let host startup logs flush
        await Task.Delay(500, stoppingToken);

        PrintBanner();

        while (!stoppingToken.IsCancellationRequested)
        {
            Console.Write("> ");
            var line = await ReadLineAsync(stoppingToken);

            if (line is null) break;

            // Empty line dismisses live status if active, otherwise ignore
            if (string.IsNullOrWhiteSpace(line)) continue;

            await HandleCommandAsync(line.Trim(), stoppingToken);
        }
    }

    #endregion

    #region Private Methods — Command Dispatch

    /// <summary>
    /// Parse and dispatch a user command.
    /// </summary>
    private async Task HandleCommandAsync(string input, CancellationToken ct)
    {
        var parts = SplitCommand(input);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "add":
                HandleAdd(parts);
                break;
            case "status" or "s":
                await HandleStatusLiveAsync(ct);
                break;
            case "progress" or "p":
                HandleProgress(parts);
                break;
            case "pause":
                HandlePause(parts);
                break;
            case "resume":
                HandleResume(parts);
                break;
            case "stop":
                HandleStop(parts);
                break;
            case "delete" or "del":
                HandleDelete(parts);
                break;
            case "download-file" or "df":
                HandleDownloadFile(parts);
                break;
            case "quit" or "q" or "exit":
                await HandleQuitAsync();
                break;
            case "help" or "h" or "?":
                PrintHelp();
                break;
            default:
                Console.WriteLine($"Unknown command: '{command}'. Type 'help' for available commands.");
                break;
        }
    }

    #endregion

    #region Private Methods — Command Handlers

    /// <summary>
    /// Handle 'add' command: add --magnet "..." or add --torrent "...".
    /// </summary>
    private void HandleAdd(string[] parts)
    {
        string? inputValue = null;

        for (var i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i] is "--magnet" or "-m")
            {
                inputValue = parts[i + 1];
                break;
            }
            if (parts[i] is "--torrent" or "-t")
            {
                inputValue = parts[i + 1];
                break;
            }
        }

        if (string.IsNullOrEmpty(inputValue))
        {
            Console.WriteLine("Usage: add --magnet \"magnet:?xt=...\" or add --torrent \"file.torrent\"");
            return;
        }

        var job = downloadManager.AddJob(inputValue);
        Console.WriteLine($"[{job.Id}] Added: {TruncateInput(inputValue)}");
        Console.WriteLine("  Processing: loading metadata → speed probe → download...");
    }

    /// <summary>
    /// Handle 'status' command: live-updating display, stops when user presses Enter.
    /// </summary>
    private async Task HandleStatusLiveAsync(CancellationToken ct)
    {
        var jobs = downloadManager.GetAllJobs();

        if (jobs.Count == 0)
        {
            Console.WriteLine("No jobs. Use 'add --magnet \"...\"' to start one.");
            return;
        }

        Console.WriteLine("Live status (press Enter to stop):");
        Console.WriteLine();

        // Start a background task that waits for Enter
        var enterPressed = new TaskCompletionSource<bool>();
        _ = Task.Run(() =>
        {
            Console.ReadLine();
            enterPressed.TrySetResult(true);
        }, ct);

        while (!ct.IsCancellationRequested && !enterPressed.Task.IsCompleted)
        {
            RenderStatusTable();

            // Wait 2 seconds or until Enter is pressed
            var delay = Task.Delay(2000, ct);
            await Task.WhenAny(delay, enterPressed.Task);
        }

        // Clear the live display line and return to prompt
        Console.WriteLine("[Status display stopped]");
    }

    /// <summary>
    /// Render the status table once (used by live status loop).
    /// </summary>
    private void RenderStatusTable()
    {
        var jobs = downloadManager.GetAllJobs();

        Console.WriteLine("\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
        foreach (var job in jobs)
        {
            var stateIcon = GetStateIcon(job.State);
            var fileDisplay = job.SkippedFileIndices.Count > 0
                ? $"{job.CompletedFiles}/{job.ActiveFileCount} files ({job.SkippedFileIndices.Count} skipped)"
                : $"{job.CompletedFiles}/{job.TotalFiles} files";
            Console.WriteLine(
                $"  [{job.Id}] {stateIcon} {job.Name} | {job.State} | {fileDisplay}");
        }
        Console.WriteLine("\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
    }

    /// <summary>
    /// Get icon for job state.
    /// </summary>
    private static string GetStateIcon(TorrentJobState state) => state switch
    {
        TorrentJobState.Probing => "\ud83d\udd0d",
        TorrentJobState.Downloading => "\u2b07\ufe0f",
        TorrentJobState.Paused => "\u23f8\ufe0f",
        TorrentJobState.Stopped => "\u23f9\ufe0f",
        TorrentJobState.Done => "\u2705",
        TorrentJobState.Failed => "\u274c",
        _ => "\u23f3"
    };

    /// <summary>
    /// Handle 'progress' command: show per-file detail for one job.
    /// </summary>
    private void HandleProgress(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var jobId))
        {
            Console.WriteLine("Usage: progress <job-id>");
            return;
        }

        var job = downloadManager.GetJob(jobId);
        if (job is null)
        {
            Console.WriteLine($"Job {jobId} not found.");
            return;
        }

        Console.WriteLine($"[{job.Id}] {job.Name} — {job.State}");

        if (job.Metadata is null)
        {
            Console.WriteLine("  Metadata not yet loaded...");
            return;
        }

        var completedPaths = job.Results.Select(r => r.FileName).ToHashSet();

        foreach (var file in job.Metadata.Files)
        {
            string status;
            if (completedPaths.Contains(file.Path))
                status = "✓ uploaded";
            else if (job.SkippedFileIndices.Contains(file.Index))
                status = "⬇ skipped";
            else
                status = "  queued";

            Console.WriteLine(
                $"  [{file.Index}] {status} | {file.Path} ({file.Size / 1024.0 / 1024.0:F1} MB)");
        }

        if (job.SkippedFileIndices.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  {job.SkippedFileIndices.Count} files skipped. Use 'download-file {job.Id} <index>' to queue them.");
        }

        if (job.Results.Count > 0)
        {
            var totalDl = TimeSpan.FromTicks(job.Results.Sum(r => r.DownloadTime.Ticks));
            var totalUl = TimeSpan.FromTicks(job.Results.Sum(r => r.UploadTime.Ticks));
            Console.WriteLine($"  Total download time: {totalDl}");
            Console.WriteLine($"  Total upload time:   {totalUl}");
        }
    }

    /// <summary>
    /// Handle 'pause' command.
    /// </summary>
    private void HandlePause(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var jobId))
        {
            Console.WriteLine("Usage: pause <job-id>");
            return;
        }

        Console.WriteLine(downloadManager.Pause(jobId)
            ? $"[{jobId}] Paused"
            : $"[{jobId}] Cannot pause (not running or not found)");
    }

    /// <summary>
    /// Handle 'resume' command.
    /// </summary>
    private void HandleResume(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var jobId))
        {
            Console.WriteLine("Usage: resume <job-id>");
            return;
        }

        Console.WriteLine(downloadManager.Resume(jobId)
            ? $"[{jobId}] Resumed"
            : $"[{jobId}] Cannot resume (not paused/stopped or not found)");
    }

    /// <summary>
    /// Handle 'stop' command.
    /// </summary>
    private void HandleStop(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var jobId))
        {
            Console.WriteLine("Usage: stop <job-id>");
            return;
        }

        Console.WriteLine(downloadManager.Stop(jobId)
            ? $"[{jobId}] Stopped (use 'resume {jobId}' to restart, 'delete {jobId}' to remove)"
            : $"[{jobId}] Cannot stop (already stopped/done or not found)");
    }

    /// <summary>
    /// Handle 'delete' command: remove stopped/done/failed jobs.
    /// </summary>
    private void HandleDelete(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var jobId))
        {
            Console.WriteLine("Usage: delete <job-id>");
            return;
        }

        Console.WriteLine(downloadManager.Delete(jobId)
            ? $"[{jobId}] Deleted"
            : $"[{jobId}] Cannot delete (still running, or not found)");
    }

    /// <summary>
    /// Handle 'download-file' command: queue skipped files for download.
    /// Usage: download-file <job-id> <file-index> [<file-index> ...]
    /// </summary>
    private void HandleDownloadFile(string[] parts)
    {
        if (parts.Length < 3)
        {
            Console.WriteLine("Usage: download-file <job-id> <file-index> [<file-index> ...]");
            Console.WriteLine("  Use 'progress <job-id>' to see file indices.");
            return;
        }

        if (!int.TryParse(parts[1], out var jobId))
        {
            Console.WriteLine("Invalid job ID.");
            return;
        }

        var fileIndices = new List<int>();
        for (var i = 2; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out var idx))
                fileIndices.Add(idx);
            else
                Console.WriteLine($"Skipping invalid index: {parts[i]}");
        }

        if (fileIndices.Count == 0)
        {
            Console.WriteLine("No valid file indices provided.");
            return;
        }

        Console.WriteLine(downloadManager.QueueFiles(jobId, fileIndices.ToArray())
            ? $"[{jobId}] Queued {fileIndices.Count} file(s) for download"
            : $"[{jobId}] Cannot queue files (job must be stopped/done/failed, or not found)");
    }

    /// <summary>
    /// Handle 'quit' command: graceful shutdown.
    /// </summary>
    private async Task HandleQuitAsync()
    {
        Console.WriteLine("Shutting down all jobs...");
        await downloadManager.ShutdownAsync();
        lifetime.StopApplication();
    }

    #endregion

    #region Private Methods — Helpers

    /// <summary>
    /// Print the daemon startup banner.
    /// </summary>
    private static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════");
        Console.WriteLine("  TorrentProject — Daemon Mode");
        Console.WriteLine("  Type 'help' for available commands.");
        Console.WriteLine("═══════════════════════════════════════════════");
        Console.WriteLine();
    }

    /// <summary>
    /// Print available commands.
    /// </summary>
    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  add --magnet \"...\"    Add a torrent by magnet link");
        Console.WriteLine("  add --torrent \"...\"   Add a torrent by .torrent file");
        Console.WriteLine("  status (s)            Live status (press Enter to stop)");
        Console.WriteLine("  progress <id> (p)     Show per-file detail for a job");
        Console.WriteLine("  pause <id>            Pause a running job");
        Console.WriteLine("  resume <id>           Resume a paused/stopped job");
        Console.WriteLine("  stop <id>             Stop a job (keeps it for resume)");
        Console.WriteLine("  delete <id> (del)     Delete a stopped/done/failed job");
        Console.WriteLine("  download-file (df)    Queue skipped files: df <job-id> <file-idx> ...");
        Console.WriteLine("  quit (q)              Shut down all jobs and exit");
        Console.WriteLine("  help (h)              Show this help");
        Console.WriteLine();
    }

    /// <summary>
    /// Read a line from stdin asynchronously.
    /// </summary>
    private static async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        return await Task.Run(Console.ReadLine, ct);
    }

    /// <summary>
    /// Split command input, respecting quoted strings.
    /// </summary>
    private static string[] SplitCommand(string input)
    {
        var parts = new List<string>();
        var inQuote = false;
        var current = new System.Text.StringBuilder();

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (c == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts.ToArray();
    }

    /// <summary>
    /// Truncate long input for display.
    /// </summary>
    private static string TruncateInput(string input)
    {
        return input.Length > 70 ? input[..70] + "..." : input;
    }

    #endregion
}

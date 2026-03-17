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
                HandleStatus();
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
    /// Handle 'status' command: show one-line summary per job.
    /// </summary>
    private void HandleStatus()
    {
        var jobs = downloadManager.GetAllJobs();

        if (jobs.Count == 0)
        {
            Console.WriteLine("No active jobs. Use 'add --magnet \"...\"' to start one.");
            return;
        }

        Console.WriteLine("─────────────────────────────────────────────────────");
        foreach (var job in jobs)
        {
            var stateIcon = job.State switch
            {
                TorrentJobState.Probing => "🔍",
                TorrentJobState.Downloading => "⬇️",
                TorrentJobState.Paused => "⏸️",
                TorrentJobState.Done => "✅",
                TorrentJobState.Failed => "❌",
                _ => "⏳"
            };

            Console.WriteLine(
                $"  [{job.Id}] {stateIcon} {job.Name} | {job.State} | {job.CompletedFiles}/{job.TotalFiles} files");
        }
        Console.WriteLine("─────────────────────────────────────────────────────");
    }

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
            var status = completedPaths.Contains(file.Path) ? "✓ uploaded" : "  queued";
            Console.WriteLine(
                $"  [{file.Index + 1}] {status} | {file.Path} ({file.Size / 1024.0 / 1024.0:F1} MB)");
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
            : $"[{jobId}] Cannot resume (not paused or not found)");
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
            ? $"[{jobId}] Stopped and removed"
            : $"[{jobId}] Not found");
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
        Console.WriteLine("  status (s)            Show all jobs");
        Console.WriteLine("  progress <id> (p)     Show per-file detail for a job");
        Console.WriteLine("  pause <id>            Pause a running job");
        Console.WriteLine("  resume <id>           Resume a paused job");
        Console.WriteLine("  stop <id>             Stop and remove a job");
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

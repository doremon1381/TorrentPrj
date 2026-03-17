namespace TorrentProject.Models;

/// <summary>
/// State of a torrent job in the download manager.
/// </summary>
public enum TorrentJobState
{
    /// <summary>Torrent added, waiting to start.</summary>
    Added,

    /// <summary>Speed probe running to determine file priority order.</summary>
    Probing,

    /// <summary>Actively downloading files.</summary>
    Downloading,

    /// <summary>All downloads paused by user.</summary>
    Paused,

    /// <summary>All files downloaded and uploaded.</summary>
    Done,

    /// <summary>An error occurred during processing.</summary>
    Failed
}

/// <summary>
/// Tracks the state and progress of a single torrent in the download manager.
/// </summary>
public sealed class TorrentJob
{
    #region Fields

    private static int _nextId;

    #endregion

    #region Properties

    /// <summary>Unique job ID (auto-incremented).</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>The torrent path or magnet URI.</summary>
    public string InputValue { get; init; } = "";

    /// <summary>Torrent name (populated after metadata loads).</summary>
    public string Name { get; set; } = "Loading...";

    /// <summary>Current job state.</summary>
    public TorrentJobState State { get; set; } = TorrentJobState.Added;

    /// <summary>Loaded torrent metadata.</summary>
    public TorrentMetadata? Metadata { get; set; }

    /// <summary>Sorted file indices from speed probe (fastest first).</summary>
    public int[]? FileOrder { get; set; }

    /// <summary>File processing results (one per completed file).</summary>
    public List<FileProcessResult> Results { get; } = [];

    /// <summary>Total files in this torrent.</summary>
    public int TotalFiles => Metadata?.Files.Count ?? 0;

    /// <summary>Number of files fully processed (downloaded + uploaded).</summary>
    public int CompletedFiles => Results.Count;

    /// <summary>Optional Google Drive folder ID for this torrent's uploads.</summary>
    public string? DriveFolderId { get; set; }

    /// <summary>Cancellation token source for pausing/stopping this job.</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Error message if the job failed.</summary>
    public string? Error { get; set; }

    #endregion
}

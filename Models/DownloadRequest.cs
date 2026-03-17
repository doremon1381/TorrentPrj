namespace TorrentProject.Models;

/// <summary>
/// Carries CLI arguments from the "download" command into the worker via DI.
/// </summary>
public sealed record DownloadRequest
{
    /// <summary>Path to a .torrent file (mutually exclusive with Magnet).</summary>
    public string? TorrentPath { get; init; }

    /// <summary>Magnet URI (mutually exclusive with TorrentPath).</summary>
    public string? Magnet { get; init; }

    /// <summary>Optional Google Drive folder ID override (from --drive-folder).</summary>
    public string? DriveFolderId { get; init; }

    /// <summary>Returns the torrent path or magnet URI, whichever was provided.</summary>
    public string InputValue => TorrentPath ?? Magnet
        ?? throw new InvalidOperationException("Either TorrentPath or Magnet must be set.");
}

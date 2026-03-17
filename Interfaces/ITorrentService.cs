using TorrentProject.Models;

namespace TorrentProject.Interfaces;

/// <summary>
/// Manages the MonoTorrent engine: load metadata, download individual files, and cleanup.
/// </summary>
public interface ITorrentService
{
    /// <summary>
    /// Load a torrent from a .torrent file path or magnet URI and return its metadata.
    /// </summary>
    Task<TorrentMetadata> LoadTorrentAsync(
        string torrentPathOrMagnet,
        CancellationToken ct = default);

    /// <summary>
    /// Download a single file from the loaded torrent.
    /// Sets this file to HIGH priority and all others to DO_NOT_DOWNLOAD.
    /// </summary>
    /// <returns>The absolute path of the downloaded file.</returns>
    Task<string> DownloadFileAsync(
        int fileIndex,
        string downloadDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gracefully stop the torrent engine and release resources.
    /// </summary>
    Task StopAsync();
}

using System.Threading.Channels;
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
    /// Probe all files for a given duration to measure per-file download speeds.
    /// Returns file indices sorted by descending speed (fastest first).
    /// </summary>
    Task<int[]> ProbeFileSpeedsAsync(
        TimeSpan duration,
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
    /// Download multiple files concurrently. Returns a ChannelReader that yields
    /// CompletedFileEvent as each file finishes downloading. The engine stays running
    /// and manages up to maxConcurrent files at a time with Priority.High.
    /// </summary>
    /// <param name="maxConcurrent">Max files downloading simultaneously.</param>
    /// <param name="fileOrder">Optional custom file order (e.g., fastest-first from speed probe). Null = sequential.</param>
    /// <param name="ct">Cancellation token.</param>
    ChannelReader<CompletedFileEvent> DownloadFilesConcurrentlyAsync(
        int maxConcurrent,
        int[]? fileOrder = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gracefully stop the torrent engine and release resources.
    /// </summary>
    Task StopAsync();
}

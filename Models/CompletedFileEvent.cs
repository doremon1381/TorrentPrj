namespace TorrentProject.Models;

/// <summary>
/// Event emitted when a file finishes downloading and is ready for upload.
/// </summary>
public sealed record CompletedFileEvent(
    int FileIndex,
    string LocalPath,
    TimeSpan DownloadTime);

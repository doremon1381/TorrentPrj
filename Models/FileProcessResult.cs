namespace TorrentProject.Models;

/// <summary>
/// Result of processing one file through the download → upload → delete pipeline.
/// </summary>
public sealed record FileProcessResult(
    string FileName,
    long FileSize,
    string DriveFileId,
    TimeSpan DownloadTime,
    TimeSpan UploadTime);

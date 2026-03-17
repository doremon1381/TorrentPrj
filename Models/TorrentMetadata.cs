namespace TorrentProject.Models;

/// <summary>
/// Metadata about the entire torrent: name, file list, and total size.
/// </summary>
public sealed record TorrentMetadata(
    string Name,
    IReadOnlyList<TorrentFileInfo> Files,
    long TotalSize);

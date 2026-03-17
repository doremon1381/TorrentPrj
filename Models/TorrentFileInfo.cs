namespace TorrentProject.Models;

/// <summary>
/// Info about a single file within a torrent.
/// </summary>
public sealed record TorrentFileInfo(
    int Index,
    string Path,
    long Size,
    string FullPath);

namespace TorrentProject.Interfaces;

/// <summary>
/// Manages uploads to Google Drive: upload files and create folders.
/// </summary>
public interface IGoogleDriveService
{
    /// <summary>
    /// Upload a local file to Google Drive using resumable upload.
    /// </summary>
    /// <returns>The Google Drive file ID of the uploaded file.</returns>
    Task<string> UploadFileAsync(
        string localFilePath,
        string? targetFolderId = null,
        string? fileName = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Create a folder on Google Drive for organizing torrent contents.
    /// </summary>
    /// <returns>The Google Drive folder ID.</returns>
    Task<string> CreateFolderAsync(
        string folderName,
        string? parentFolderId = null,
        CancellationToken ct = default);
}

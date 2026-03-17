using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TorrentProject.Configuration;
using TorrentProject.Interfaces;

namespace TorrentProject.Services;

/// <summary>
/// Google Drive upload service using resumable uploads with chunking.
/// </summary>
public sealed class GoogleDriveService : IGoogleDriveService
{
    #region Constants

    /// <summary>
    /// Percentage interval for upload progress logging (every 10%).
    /// </summary>
    private const int LogIntervalPercent = 10;

    #endregion

    #region Fields

    private readonly ILogger<GoogleDriveService> _logger;
    private readonly GoogleDriveSettings _settings;
    private readonly GoogleAuthService _authService;
    private DriveService? _driveService;

    #endregion

    #region Constructor

    public GoogleDriveService(
        GoogleAuthService authService,
        IOptions<GoogleDriveSettings> settings,
        ILogger<GoogleDriveService> logger)
    {
        _authService = authService;
        _settings = settings.Value;
        _logger = logger;
    }

    #endregion

    #region Public Methods

    /// <inheritdoc />
    public async Task<string> UploadFileAsync(
        string localFilePath,
        string? targetFolderId = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        var driveService = await GetDriveServiceAsync(ct);
        var fileName = Path.GetFileName(localFilePath);
        var fileSize = new FileInfo(localFilePath).Length;
        var folderId = targetFolderId ?? _settings.TargetFolderId;

        _logger.LogInformation(
            "Uploading: {FileName} ({Size:F2} MB) → Drive folder {FolderId}",
            fileName, fileSize / 1024.0 / 1024.0,
            string.IsNullOrEmpty(folderId) ? "(root)" : folderId);

        var request = await CreateUploadRequestAsync(
            driveService, localFilePath, fileName, folderId);

        AttachProgressHandler(request, fileSize, progress);

        var result = await request.UploadAsync(ct);

        if (result.Status == UploadStatus.Failed)
        {
            throw new InvalidOperationException(
                $"Upload failed for '{fileName}': {result.Exception?.Message}",
                result.Exception);
        }

        var driveFileId = request.ResponseBody.Id;
        var webLink = request.ResponseBody.WebViewLink;

        _logger.LogInformation(
            "Upload complete: {FileName} → Drive ID: {DriveId} | Link: {Link}",
            fileName, driveFileId, webLink ?? "N/A");

        return driveFileId;
    }

    /// <inheritdoc />
    public async Task<string> CreateFolderAsync(
        string folderName,
        string? parentFolderId = null,
        CancellationToken ct = default)
    {
        var driveService = await GetDriveServiceAsync(ct);
        var folderId = parentFolderId ?? _settings.TargetFolderId;

        var folderMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = folderName,
            MimeType = "application/vnd.google-apps.folder",
            Parents = !string.IsNullOrEmpty(folderId) ? [folderId] : null
        };

        var request = driveService.Files.Create(folderMetadata);
        request.Fields = "id, name";

        var folder = await request.ExecuteAsync(ct);

        _logger.LogInformation("Created Drive folder: {Name} → ID: {Id}", folder.Name, folder.Id);
        return folder.Id;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Lazily authenticate and cache the DriveService instance.
    /// </summary>
    private async Task<DriveService> GetDriveServiceAsync(CancellationToken ct)
    {
        _driveService ??= await _authService.AuthenticateAsync(_settings, ct);
        return _driveService;
    }

    /// <summary>
    /// Create a resumable upload request for a local file.
    /// </summary>
    private async Task<FilesResource.CreateMediaUpload> CreateUploadRequestAsync(
        DriveService driveService, string localFilePath, string fileName, string? folderId)
    {
        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = fileName,
            Parents = !string.IsNullOrEmpty(folderId) ? [folderId] : null
        };

        var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);
        var mimeType = GetMimeType(localFilePath);

        var request = driveService.Files.Create(fileMetadata, stream, mimeType);
        request.Fields = "id, name, size, webViewLink";
        request.ChunkSize = _settings.ChunkSizeMB * 1024 * 1024;

        return request;
    }

    /// <summary>
    /// Attach a progress handler that logs upload percentage at regular intervals.
    /// </summary>
    private void AttachProgressHandler(
        FilesResource.CreateMediaUpload request, long fileSize, IProgress<long>? progress)
    {
        var lastLoggedPercent = -1;

        request.ProgressChanged += p =>
        {
            progress?.Report(p.BytesSent);

            var percent = fileSize > 0 ? (int)(p.BytesSent * 100 / fileSize) : 0;

            if (percent / LogIntervalPercent > lastLoggedPercent / LogIntervalPercent)
            {
                lastLoggedPercent = percent;
                _logger.LogInformation(
                    "  Upload: {Percent}% | {Sent:F1} / {Total:F1} MB",
                    percent, p.BytesSent / 1024.0 / 1024.0, fileSize / 1024.0 / 1024.0);
            }
        };
    }

    /// <summary>
    /// Map file extension to MIME type for the Drive upload.
    /// </summary>
    private static string GetMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".wmv" => "video/x-ms-wmv",
            ".flv" => "video/x-flv",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".zip" => "application/zip",
            ".rar" => "application/x-rar-compressed",
            ".7z" => "application/x-7z-compressed",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".pdf" => "application/pdf",
            ".iso" => "application/x-iso9660-image",
            ".srt" => "text/plain",
            ".sub" => "text/plain",
            ".ass" => "text/plain",
            ".txt" => "text/plain",
            ".nfo" => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }

    #endregion
}

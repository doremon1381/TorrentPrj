namespace TorrentProject.Configuration;

/// <summary>
/// Configuration for Google Drive authentication and upload.
/// Bound from the "GoogleDriveSettings" section of appsettings.json.
/// </summary>
public sealed record GoogleDriveSettings
{
    /// <summary>Path to the Service Account JSON key file (primary, VPS).</summary>
    public string ServiceAccountKeyPath { get; init; } = "./service-account.json";

    /// <summary>Path to the OAuth2 client secret JSON (local dev fallback).</summary>
    public string CredentialsPath { get; init; } = "./credentials.json";

    /// <summary>Directory for cached OAuth2 refresh tokens.</summary>
    public string TokenStorePath { get; init; } = "./tokens";

    /// <summary>Google Drive folder ID to upload files into. Required for Service Account.</summary>
    public string TargetFolderId { get; init; } = "";

    /// <summary>Resumable upload chunk size in MB. Must be ≥ 8 MB.</summary>
    public int ChunkSizeMB { get; init; } = 10;
}

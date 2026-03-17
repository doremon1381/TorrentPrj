---
name: google-drive-upload
description: How to authenticate and upload files to Google Drive using the official .NET SDK. Supports Service Account (headless VPS) and OAuth2 (local dev).
---

# Google Drive Upload Skill

## Overview
Upload files to Google Drive using **Google.Apis.Drive.v3**. Two auth methods supported:
- **Service Account** (primary) — for headless VPS, no browser needed, tokens never expire
- **OAuth2 User Credentials** (fallback) — for local development with browser

## NuGet Packages
```xml
<PackageReference Include="Google.Apis.Drive.v3" Version="1.*" />
<PackageReference Include="Google.Apis.Auth" Version="1.*" />
```

---

## Authentication Method 1: Service Account (Recommended for VPS)

A Service Account is a special Google account that belongs to your application, not a person.
**No browser needed. No tokens to expire. Works headless indefinitely.**

### Setup (one-time)
1. Go to https://console.cloud.google.com/
2. Create project → Enable **Google Drive API**
3. **IAM & Admin** → **Service Accounts** → Create Service Account
4. Name it (e.g., `torrent-uploader`) → Create → Done
5. Click the service account → **Keys** tab → **Add Key** → **Create new key** → **JSON**
6. Download the JSON → save as `service-account.json` in project root
7. **Share your target Google Drive folder** with the service account email (`torrent-uploader@project-id.iam.gserviceaccount.com`)
   - Right-click folder in Drive → Share → paste the email → set to **Editor**

### Code
```csharp
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

public sealed class GoogleAuthService
{
    private const string ApplicationName = "TorrentProject";
    private static readonly string[] Scopes = { DriveService.Scope.DriveFile };

    /// <summary>
    /// Authenticate using a Service Account key file (headless, no browser).
    /// The service account must have access to the target Drive folder.
    /// </summary>
    public async Task<DriveService> AuthenticateWithServiceAccountAsync(
        string serviceAccountKeyPath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            serviceAccountKeyPath, FileMode.Open, FileAccess.Read);

        var credential = GoogleCredential
            .FromStreamAsync(stream, cancellationToken)
            .Result
            .CreateScoped(Scopes);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
    }
}
```

> **Important**: Files uploaded by a Service Account are "owned" by that account.
> To see them in your personal Drive, you **must** share the target folder with the
> service account email. Files uploaded into that shared folder will appear for you.

### How Sharing Works

```
Your Google Drive
└── Torrents/                         ← Shared with service-account@...
    ├── ep01.mkv  (uploaded by SA)    ← You can see and manage this
    ├── ep02.mkv  (uploaded by SA)    ← You can see and manage this
    └── ep03.mkv  (uploaded by SA)    ← You can see and manage this
```

---

## Authentication Method 2: OAuth2 User Credentials (Local Dev / Fallback)

Uses your personal Google account. Requires a browser on first run. Good for local
development but **NOT recommended for long-term VPS** due to token expiration risks.

### Token Expiration Rules

| App Status | Refresh Token Lifespan | Notes |
|---|---|---|
| **Testing** (default) | **7 days** | Must re-auth weekly — impractical for VPS |
| **Production** (published) | **No expiry** | As long as app is used within 6 months |

### Setup (one-time)
1. Go to https://console.cloud.google.com/
2. Create project → Enable **Google Drive API**
3. **Credentials** → Create **OAuth 2.0 Client ID** → Type: **Desktop App**
4. Download JSON → save as `credentials.json` in project root
5. (For long-term use) **OAuth consent screen** → **PUBLISH APP** to avoid 7-day token expiry

### Code
```csharp
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

public sealed class GoogleAuthService
{
    private const string ApplicationName = "TorrentProject";
    private static readonly string[] Scopes = { DriveService.Scope.DriveFile };

    /// <summary>
    /// Authenticate using OAuth2 user credentials (opens browser on first run).
    /// Tokens are cached in tokenStorePath for automatic refresh.
    /// </summary>
    public async Task<DriveService> AuthenticateWithOAuth2Async(
        string credentialsPath,
        string tokenStorePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            credentialsPath, FileMode.Open, FileAccess.Read);

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            (await GoogleClientSecrets.FromStreamAsync(stream, cancellationToken)).Secrets,
            Scopes,
            "user",
            cancellationToken,
            new FileDataStore(tokenStorePath, true));

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
    }
}
```

---

## Combined Auth Service (Supports Both Methods)

```csharp
public sealed class GoogleAuthService
{
    private const string ApplicationName = "TorrentProject";
    private static readonly string[] Scopes = { DriveService.Scope.DriveFile };

    /// <summary>
    /// Auto-detect auth method: use service-account.json if it exists,
    /// otherwise fall back to OAuth2 credentials.json.
    /// </summary>
    public async Task<DriveService> AuthenticateAsync(
        GoogleDriveSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(settings.ServiceAccountKeyPath))
        {
            return await AuthenticateWithServiceAccountAsync(
                settings.ServiceAccountKeyPath, cancellationToken);
        }

        if (File.Exists(settings.CredentialsPath))
        {
            return await AuthenticateWithOAuth2Async(
                settings.CredentialsPath, settings.TokenStorePath, cancellationToken);
        }

        throw new FileNotFoundException(
            "No auth credentials found. Provide either " +
            $"'{settings.ServiceAccountKeyPath}' (service account) or " +
            $"'{settings.CredentialsPath}' (OAuth2).");
    }

    private async Task<DriveService> AuthenticateWithServiceAccountAsync(
        string keyPath, CancellationToken ct)
    {
        await using var stream = new FileStream(keyPath, FileMode.Open, FileAccess.Read);
        var credential = (await GoogleCredential.FromStreamAsync(stream, ct))
            .CreateScoped(Scopes);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
    }

    private async Task<DriveService> AuthenticateWithOAuth2Async(
        string credentialsPath, string tokenStorePath, CancellationToken ct)
    {
        await using var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            (await GoogleClientSecrets.FromStreamAsync(stream, ct)).Secrets,
            Scopes, "user", ct,
            new FileDataStore(tokenStorePath, true));

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
    }
}
```

---

## Upload Patterns

### Resumable Upload (large files – recommended)
```csharp
public async Task<string> UploadLargeFileAsync(
    DriveService driveService,
    string localFilePath,
    string? parentFolderId = null,
    int chunkSizeMB = 10,
    IProgress<long>? progress = null,
    CancellationToken cancellationToken = default)
{
    var fileMetadata = new Google.Apis.Drive.v3.Data.File
    {
        Name = Path.GetFileName(localFilePath),
        Parents = parentFolderId != null ? new List<string> { parentFolderId } : null
    };

    await using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);
    var mimeType = GetMimeType(localFilePath);

    var request = driveService.Files.Create(fileMetadata, stream, mimeType);
    request.Fields = "id, name, size, webViewLink";
    // ChunkSize MUST be a multiple of 256 KB (256 * 1024). Any whole-number MB value is valid.
    // Google recommends at least 8 MB for performance. Default in SDK is 10 MB.
    request.ChunkSize = chunkSizeMB * 1024 * 1024;

    request.ProgressChanged += p =>
    {
        progress?.Report(p.BytesSent);
    };

    var result = await request.UploadAsync(cancellationToken);

    if (result.Status == Google.Apis.Upload.UploadStatus.Failed)
        throw new Exception($"Upload failed: {result.Exception?.Message}");

    return request.ResponseBody.Id;
}
```

### MIME Type Helper
```csharp
private static string GetMimeType(string filePath)
{
    var extension = Path.GetExtension(filePath).ToLowerInvariant();
    return extension switch
    {
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".zip" => "application/zip",
        ".rar" => "application/x-rar-compressed",
        ".pdf" => "application/pdf",
        ".iso" => "application/x-iso9660-image",
        ".srt" => "text/plain",
        ".sub" => "text/plain",
        _ => "application/octet-stream"
    };
}
```

## Creating Folders
```csharp
public async Task<string> CreateFolderAsync(
    DriveService driveService,
    string folderName,
    string? parentFolderId = null,
    CancellationToken cancellationToken = default)
{
    var folderMetadata = new Google.Apis.Drive.v3.Data.File
    {
        Name = folderName,
        MimeType = "application/vnd.google-apps.folder",
        Parents = parentFolderId != null ? new List<string> { parentFolderId } : null
    };

    var request = driveService.Files.Create(folderMetadata);
    request.Fields = "id, name";

    var folder = await request.ExecuteAsync(cancellationToken);
    return folder.Id;
}
```

## Error Handling
- Handle `TokenResponseException` for expired/revoked tokens (OAuth2 only)
- Handle `Google.GoogleApiException` for quota limits (403) and not-found (404)
- Resumable uploads auto-retry on transient failures
- Service Account credentials never expire — no token management needed

## Tips
- Use `DriveService.Scope.DriveFile` (not `.Drive`) for minimal permissions
- Service Account: share the target folder with the SA email to see uploads
- OAuth2: publish the consent screen for tokens that don't expire after 7 days
- For very large files (> 5 GB), use chunk sizes of 10-50 MB
- The `webViewLink` field in results gives you a shareable URL

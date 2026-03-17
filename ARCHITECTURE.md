# TorrentProject – Architecture Deep Dive

## Problem

Download torrent files and save them to Google Drive, with only **~30GB of local disk space**.

A naive approach (download entire torrent → upload → delete) would fail for large torrents that exceed available disk.

## Solution: Sequential Per-File Pipeline

Process **one file at a time** from the torrent, immediately uploading and deleting before moving to the next. Max disk usage = **largest single file** in the torrent, not the total torrent size.

---

## System Overview

```
┌──────────────────────────────────────────────────────────────────────┐
│                        TorrentWorker                                 │
│                     (BackgroundService)                               │
│                                                                      │
│  ┌─────────────┐                                                     │
│  │ Parse input  │ ← --torrent "file.torrent"                         │
│  │ (file/magnet)│ ← --magnet  "magnet:?xt=..."                       │
│  └──────┬──────┘                                                     │
│         ▼                                                            │
│  ┌─────────────────┐                                                 │
│  │ MonoTorrent      │  Enumerate files in torrent                     │
│  │ Load Metadata    │  [file1.mkv (4GB), file2.mkv (3GB), sub.srt]   │
│  └──────┬──────────┘                                                 │
│         ▼                                                            │
│  ┌──────────────────────────── PER-FILE LOOP ──────────────────────┐ │
│  │                                                                  │ │
│  │  ┌──────────────────┐                                            │ │
│  │  │ Set file priority │  current = HIGH, others = DO_NOT_DOWNLOAD │ │
│  │  └───────┬──────────┘                                            │ │
│  │          ▼                                                       │ │
│  │  ┌──────────────────┐                                            │ │
│  │  │ Download file     │  MonoTorrent → ./temp/file1.mkv            │ │
│  │  │ (poll progress)   │  "Progress: 45.2% | 2.1 MB/s | 15 peers" │ │
│  │  └───────┬──────────┘                                            │ │
│  │          ▼                                                       │ │
│  │  ┌──────────────────┐                                            │ │
│  │  │ Upload to Drive   │  Resumable upload in 10MB chunks          │ │
│  │  │ (track progress)  │  "Upload: 72% | 3.5 MB/s"                │ │
│  │  └───────┬──────────┘                                            │ │
│  │          ▼                                                       │ │
│  │  ┌──────────────────┐                                            │ │
│  │  │ Delete temp file  │  Free disk for next file                  │ │
│  │  └───────┬──────────┘                                            │ │
│  │          ▼                                                       │ │
│  │     Next file...                                                 │ │
│  └──────────────────────────────────────────────────────────────────┘ │
│         ▼                                                            │
│  ┌─────────────┐                                                     │
│  │ Shutdown     │  lifetime.StopApplication()                        │
│  └─────────────┘                                                     │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Component Architecture

```
┌──────────────────────────────────────────────────┐
│                   Program.cs                      │
│                                                  │
│  Host.CreateApplicationBuilder(args)             │
│  ├── Configure<TorrentSettings>                  │
│  ├── Configure<GoogleDriveSettings>              │
│  ├── AddSingleton<GoogleAuthService>             │
│  ├── AddSingleton<ITorrentService>               │
│  ├── AddSingleton<IGoogleDriveService>           │
│  └── AddHostedService<TorrentWorker>             │
└──────────────────┬───────────────────────────────┘
                   │ DI injects into
                   ▼
┌──────────────────────────────────────────────────┐
│             TorrentWorker                         │
│          (BackgroundService)                      │
│                                                  │
│  Dependencies:                                   │
│  ├── ITorrentService                             │
│  ├── IGoogleDriveService                         │
│  ├── IOptions<TorrentSettings>                   │
│  ├── IOptions<GoogleDriveSettings>               │
│  ├── ILogger<TorrentWorker>                      │
│  └── IHostApplicationLifetime                    │
│                                                  │
│  Orchestrates: parse → download → upload → clean │
└──────┬────────────────────────┬──────────────────┘
       │                        │
       ▼                        ▼
┌────────────────┐    ┌─────────────────────┐
│ TorrentService │    │ GoogleDriveService   │
│                │    │                     │
│ Implements:    │    │ Implements:         │
│ ITorrentService│    │ IGoogleDriveService │
│                │    │                     │
│ Uses:          │    │ Uses:               │
│ MonoTorrent    │    │ Google.Apis.Drive.v3│
│ ClientEngine   │    │ GoogleAuthService   │
└────────────────┘    └─────────────────────┘
```

---

## Data Flow (Per File)

```
Step 1: PRIORITY MANAGEMENT
─────────────────────────────────
Torrent contains: [ep01.mkv, ep02.mkv, ep03.mkv, subs.srt]

Round 1: ep01.mkv = HIGH,   others = DO_NOT_DOWNLOAD
Round 2: ep02.mkv = HIGH,   others = DO_NOT_DOWNLOAD
Round 3: ep03.mkv = HIGH,   others = DO_NOT_DOWNLOAD
Round 4: subs.srt = HIGH,   others = DO_NOT_DOWNLOAD


Step 2: DOWNLOAD (MonoTorrent)
─────────────────────────────────
Peers ──► ClientEngine ──► TorrentManager ──► ./temp/ep01.mkv
                                    │
                              Progress events:
                              45.2% | 2.1 MB/s | 15 peers


Step 3: UPLOAD (Google Drive API)
─────────────────────────────────
./temp/ep01.mkv ──► FileStream ──► ResumableUpload ──► Google Drive
     4 GB                   10 MB chunks
                                    │
                              Progress events:
                              Chunk 47/400 | 72% | 3.5 MB/s

     Result: Drive File ID = "1xAbC..."


Step 4: CLEANUP
─────────────────────────────────
Delete ./temp/ep01.mkv ──► 4 GB freed
Disk usage returns to ~0

──► Repeat from Step 1 with ep02.mkv
```

---

## Interface Contracts

```csharp
// ITorrentService — manages the MonoTorrent engine
public interface ITorrentService
{
    // Load a torrent and return metadata (file list, sizes)
    Task<TorrentMetadata> LoadTorrentAsync(
        string torrentPathOrMagnet,
        CancellationToken ct = default);

    // Download a single file from the torrent
    // Sets this file to HIGH priority, others to DO_NOT_DOWNLOAD
    Task<string> DownloadFileAsync(
        int fileIndex,
        string downloadDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    // Cleanup engine resources
    Task StopAsync();
}

// IGoogleDriveService — manages uploads to Drive
public interface IGoogleDriveService
{
    // Upload a local file to Google Drive
    Task<string> UploadFileAsync(
        string localFilePath,
        string? targetFolderId = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default);

    // Create a folder on Drive (for organizing torrent contents)
    Task<string> CreateFolderAsync(
        string folderName,
        string? parentFolderId = null,
        CancellationToken ct = default);
}
```

---

## Models

```csharp
// Metadata about the entire torrent
public sealed record TorrentMetadata(
    string Name,                           // Torrent name
    IReadOnlyList<TorrentFileInfo> Files,   // All files in torrent
    long TotalSize);                        // Total size in bytes

// Info about a single file within the torrent
public sealed record TorrentFileInfo(
    int Index,           // File index in torrent
    string Path,         // Relative file path
    long Size,           // Size in bytes
    string FullPath);    // Absolute path when downloaded

// Result of processing one file
public sealed record FileProcessResult(
    string FileName,
    long FileSize,
    string DriveFileId,
    TimeSpan DownloadTime,
    TimeSpan UploadTime);
```

---

## Disk Usage Analysis

```
Scenario: 50 GB torrent with 20 episodes × 2.5 GB each

Option B (original):     Needs 50 GB free ← IMPOSSIBLE on 30 GB HDD
Hybrid (per-file):       Needs  2.5 GB free ← FITS EASILY

Scenario: 25 GB single file (e.g., game ISO)

Option B (original):     Needs 25 GB free ← BARELY fits, risky
Hybrid (per-file):       Needs 25 GB free ← Same, no benefit for single files

Scenario: 35 GB single file

Both approaches:         Needs 35 GB free ← IMPOSSIBLE on 30 GB HDD
                         → Must use Option A (streaming) for this case
```

> **Limitation**: If a single file in the torrent exceeds available disk space, this approach cannot work. That's when a true streaming (Option A) approach is needed.

---

## Error Handling Strategy

```
Download fails?
├── MonoTorrent has built-in retry via piece re-request
├── If no seeders: log warning, wait, keep trying
└── If cancelled: StopAsync → cleanup temp → exit

Upload fails?
├── Google SDK auto-retries transient errors (5xx, timeouts)
├── Resumable upload can be resumed from last successful chunk
├── If auth expires: GoogleAuthService refreshes token automatically
└── If quota exceeded: log error, wait, retry

Delete fails?
├── Log warning (non-fatal)
└── Continue to next file (orphaned temp file is acceptable)

Any unexpected exception?
├── Log error with full stack trace
├── StopAsync on MonoTorrent engine
├── lifetime.StopApplication()
└── Exit with error code
```

---

## Configuration

```json
{
  "TorrentSettings": {
    "TempDownloadPath": "./temp",
    "MaxConnections": 60,
    "AllowPortForwarding": true,
    "AutoSaveLoadFastResume": true,
    "AutoSaveLoadDhtCache": true
  },
  "GoogleDriveSettings": {
    "CredentialsPath": "./credentials.json",
    "TokenStorePath": "./tokens",
    "TargetFolderId": "",
    "ChunkSizeMB": 10
  }
}
```

| Setting | Purpose | Default |
|---|---|---|
| `TempDownloadPath` | Where MonoTorrent writes files | `./temp` |
| `MaxConnections` | Max peers per torrent | `60` |
| `AllowPortForwarding` | UPnP/NAT-PMP for better connectivity | `true` |
| `AutoSaveLoadFastResume` | Skip re-hashing on restart | `true` |
| `AutoSaveLoadDhtCache` | Faster peer discovery on restart | `true` |
| `CredentialsPath` | Google OAuth2 client secret JSON | `./credentials.json` |
| `TokenStorePath` | Cached refresh tokens | `./tokens` |
| `TargetFolderId` | Drive folder to upload into (empty = root) | `""` |
| `ChunkSizeMB` | Resumable upload chunk size (must be ≥ 8 MB) | `10` |

---

## CLI Architecture

The app uses **System.CommandLine** for structured verb-based CLI parsing, designed for headless VPS operation.

### Command Structure

```
TorrentProject
├── download                  # Main workflow: download → upload → delete
│   ├── --torrent <path>      # Path to .torrent file
│   ├── --magnet <link>       # Magnet URI
│   └── --drive-folder <id>   # Target Google Drive folder ID
│
├── auth                      # Authenticate with Google (needs browser)
│                             # Run on local machine, copy tokens to VPS
│
└── list-files                # Inspect torrent contents (no download)
    ├── --torrent <path>
    └── --magnet <link>
```

### NuGet Package

```xml
<PackageReference Include="System.CommandLine" Version="2.*" />
```

### Command Flow

```
Program.cs
    │
    ├── Parse CLI args via System.CommandLine
    │
    ├── "download" verb
    │   └── Start Host with TorrentWorker (BackgroundService)
    │       └── Per-file loop: download → upload → delete → next
    │
    ├── "auth" verb
    │   └── Run GoogleAuthService.AuthenticateAsync()
    │       └── Opens browser → saves token to ./tokens/
    │
    └── "list-files" verb
        └── Load torrent metadata via MonoTorrent
            └── Print file list to console → exit
```

---

## Headless VPS Deployment

### OAuth2 Token Flow (Headless)

```
┌─────────────────────────────┐
│    LOCAL MACHINE (browser)   │
│                             │
│  1. dotnet run -- auth      │
│  2. Browser opens           │
│  3. User grants consent     │
│  4. Token saved to ./tokens │
└──────────┬──────────────────┘
           │ scp ./tokens/ ./credentials.json
           ▼
┌─────────────────────────────┐
│    VPS (headless, no GUI)    │
│                             │
│  ./tokens/                  │
│    └── Google.Apis.Auth...  │  ← Contains refresh token
│  ./credentials.json         │  ← Client ID + secret
│                             │
│  Token auto-refreshes       │
│  indefinitely via refresh   │
│  token (no browser needed)  │
└─────────────────────────────┘
```

> **Important**: The refresh token does NOT expire unless you revoke it in Google Cloud Console or the app is unused for 6 months (for apps in "Testing" status). For production, publish the OAuth consent screen to avoid the 6-month expiry.

### VPS Deployment Architecture

```
/opt/torrentproject/
├── TorrentProject              # Self-contained Linux binary
├── appsettings.json            # Configuration
├── credentials.json            # Google OAuth2 client secret
├── tokens/                     # Cached OAuth2 refresh token
│   └── Google.Apis.Auth...
└── temp/                       # Temporary download directory
    └── (files appear and disappear during processing)
```

### Publish Command

```bash
# From dev machine (Windows)
dotnet publish -c Release -r linux-x64 --self-contained -o ./publish

# For ARM-based VPS (e.g., Oracle Cloud free tier)
dotnet publish -c Release -r linux-arm64 --self-contained -o ./publish
```

### systemd Service

```ini
[Unit]
Description=TorrentProject - Download to Google Drive
After=network.target

[Service]
Type=simple
User=torrent
WorkingDirectory=/opt/torrentproject
ExecStart=/opt/torrentproject/TorrentProject download --torrent "/path/to/file.torrent"
Restart=on-failure
RestartSec=10
StandardOutput=journal
StandardError=journal
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

### Common VPS Operations

```bash
# Deploy
scp -r ./publish/* user@vps:/opt/torrentproject/
ssh user@vps "chmod +x /opt/torrentproject/TorrentProject"

# Run one-off download
ssh user@vps "/opt/torrentproject/TorrentProject download --magnet 'magnet:?xt=...'"

# Run as service
sudo systemctl start torrentproject

# Check status & logs
sudo systemctl status torrentproject
journalctl -u torrentproject -f --no-pager -n 50

# Stop
sudo systemctl stop torrentproject
```

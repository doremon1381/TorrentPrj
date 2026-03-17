# TorrentProject – Architecture Deep Dive

## Problem

Download torrent files and save them to Google Drive, with only **~30GB of local disk space**.

A naive approach (download entire torrent → upload → delete) would fail for large torrents that exceed available disk.

## Solution: Concurrent Per-File Pipeline with Speed-Based Prioritization

Process files from the torrent concurrently (up to N at a time), with **fastest files prioritized first** via an initial speed probe. Each completed file is immediately uploaded to Google Drive and deleted locally. Max disk usage = **sum of N largest concurrent files**, not the total torrent size.

---

## System Overview

### One-Shot Mode (download verb)

```
┌──────────────────────────────────────────────────────────────────────┐
│                        TorrentWorker                                 │
│                     (BackgroundService)                               │
│                                                                      │
│  ┌─────────────┐                                                     │
│  │ Parse input  │ ← --torrent / --magnet / --concurrent              │
│  └──────┬──────┘                                                     │
│         ▼                                                            │
│  ┌─────────────────┐                                                 │
│  │ Load Metadata    │  [ep01..ep13, subs (13 files)]                 │
│  └──────┬──────────┘                                                 │
│         ▼                                                            │
│  ┌─────────────────┐                                                 │
│  │ Speed Probe (30s)│  Probe all files → rank by bytes/sec           │
│  │                  │  Result: [ep05, ep02, ep09, ..., ep07]          │
│  └──────┬──────────┘                                                 │
│         ▼                                                            │
│  ┌──────────────────── CONCURRENT DOWNLOAD (up to 6) ─────────────┐ │
│  │  Slot 1: ep05 → downloading 67%                                  │ │
│  │  Slot 2: ep02 → downloading 34%                                  │ │
│  │  Slot 3: ep09 → downloading 12%                                  │ │
│  │  Slot 4: (waiting for slot)                                      │ │
│  │                                                                  │ │
│  │  On completion → Channel<CompletedFileEvent> → upload → delete    │ │
│  └──────────────────────────────────────────────────────────────────┘ │
│         ▼                                                            │
│  ┌─────────────┐                                                     │
│  │ Shutdown     │  lifetime.StopApplication()                        │
│  └─────────────┘                                                     │
└──────────────────────────────────────────────────────────────────────┘
```

### Daemon Mode (daemon verb)

```
┌──────────────────────────────────────────────────────────────────────┐
│  Program.cs ("daemon" verb)                                          │
│                                                                      │
│  ┌────────────────────┐    ┌───────────────────────────────────────┐ │
│  │  CommandHandler     │    │  DownloadManager                     │ │
│  │  (stdin loop)       │───▶│                                     │ │
│  │                     │    │  Manages List<TorrentJob>            │ │
│  │  add / status /     │◀───│  Per-job: probe → download → upload │ │
│  │  progress / pause / │    │  Per-job CancellationTokenSource     │ │
│  │  resume / stop /    │    │  Progress on-demand only             │ │
│  │  quit               │    │                                     │ │
│  └────────────────────┘    └───────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Component Architecture

```
┌──────────────────────────────────────────────────┐
│                   Program.cs                      │
│                                                  │
│  "download" verb (one-shot):                     │
│  ├── Configure<TorrentSettings>                  │
│  ├── Configure<GoogleDriveSettings>              │
│  ├── AddSingleton<ITorrentService>               │
│  ├── AddSingleton<IGoogleDriveService>           │
│  └── AddHostedService<TorrentWorker>             │
│                                                  │
│  "daemon" verb (interactive):                    │
│  ├── Configure<TorrentSettings>                  │
│  ├── Configure<GoogleDriveSettings>              │
│  ├── AddSingleton<IGoogleDriveService>           │
│  ├── AddSingleton<DownloadManager>               │
│  └── AddHostedService<CommandHandler>            │
└──────────────────┬───────────────────────────────┘
                   │ DI injects into
                   ▼
┌────────────────┐  ┌───────────────┐  ┌──────────────────┐
│ TorrentService │  │ DownloadManager│  │ CommandHandler   │
│                │  │               │  │ (stdin loop)     │
│ Implements:    │  │ Manages N     │  │                  │
│ ITorrentService│  │ TorrentJobs   │  │ Dispatches to    │
│                │  │               │  │ DownloadManager  │
│ Features:      │  │ Per-job CTS   │  └──────────────────┘
│ • Load torrent │  │ Per-job       │
│ • Speed probe  │  │ TorrentService│  ┌──────────────────┐
│ • Single DL    │  │ instance      │  │ GoogleDriveService│
│ • Concurrent   │  └───────────────┘  │                  │
│   DL (Channel) │                     │ Resumable upload │
└────────────────┘                     │ GoogleAuthService│
                                       └──────────────────┘
```

---

## Data Flow

```
Step 1: SPEED PROBE (30 seconds)
─────────────────────────────────
Torrent contains: [ep01..ep13, subs.srt]
All files → Priority.Normal → start engine → measure bytes/sec per file

Result (sorted fastest first):
  [1] ep05: 1.8 MB/s
  [2] ep02: 1.2 MB/s
  [3] ep09: 0.9 MB/s
  ...
  [13] ep07: 0.01 MB/s


Step 2: CONCURRENT DOWNLOAD (up to 6 slots)
─────────────────────────────────
Slot 1: ep05 = HIGH   ▶ downloading
Slot 2: ep02 = HIGH   ▶ downloading
Slot 3: ep09 = HIGH   ▶ downloading
Slot 4: ep01 = HIGH   ▶ downloading
Slot 5: ep11 = HIGH   ▶ downloading
Slot 6: ep03 = HIGH   ▶ downloading
Remaining = DO_NOT_DOWNLOAD (queued)

  As file completes → Channel<CompletedFileEvent> → next file fills slot


Step 3: UPLOAD (Google Drive API)
─────────────────────────────────
Completed file → ResumableUpload → 10 MB chunks → Google Drive
  Result: Drive File ID = "1xAbC..."


Step 4: CLEANUP
─────────────────────────────────
Delete temp file → disk freed → slot available for next file
```

---

## Interface Contracts

```csharp
// ITorrentService — manages the MonoTorrent engine
public interface ITorrentService
{
    Task<TorrentMetadata> LoadTorrentAsync(string torrentPathOrMagnet, CancellationToken ct);

    // Probe all files for given duration, return indices sorted by speed (fastest first)
    Task<int[]> ProbeFileSpeedsAsync(TimeSpan duration, CancellationToken ct);

    // Download a single file (HIGH priority, others DO_NOT_DOWNLOAD)
    Task<string> DownloadFileAsync(int fileIndex, string downloadDirectory, ...);

    // Download concurrently, yielding CompletedFileEvent via Channel as each finishes
    ChannelReader<CompletedFileEvent> DownloadFilesConcurrentlyAsync(
        int maxConcurrent, int[]? fileOrder = null, CancellationToken ct = default);

    Task StopAsync();
}

// IGoogleDriveService — manages uploads to Drive
public interface IGoogleDriveService
{
    Task<string> UploadFileAsync(string localFilePath, string? targetFolderId, ...);
    Task<string> CreateFolderAsync(string folderName, string? parentFolderId, ...);
}
```

---

## Models

```csharp
public sealed record TorrentMetadata(string Name, IReadOnlyList<TorrentFileInfo> Files, long TotalSize);
public sealed record TorrentFileInfo(int Index, string Path, long Size, string FullPath);
public sealed record FileProcessResult(string FileName, long FileSize, string DriveFileId,
    TimeSpan DownloadTime, TimeSpan UploadTime);
public sealed record CompletedFileEvent(int FileIndex, string LocalPath, TimeSpan DownloadTime);
public sealed record DownloadRequest { ... TorrentPath, Magnet, DriveFolderId, MaxConcurrentFiles }

// State machine for daemon mode
public enum TorrentJobState { Added, Probing, Downloading, Paused, Done, Failed }
public sealed class TorrentJob
{
    int Id;                          // Auto-incremented
    string Name;                     // Torrent name
    TorrentJobState State;           // Current state
    TorrentMetadata? Metadata;
    int[]? FileOrder;                // Speed-sorted file indices
    List<FileProcessResult> Results; // Completed files
    CancellationTokenSource Cts;     // Per-job cancellation
}
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
    "AutoSaveLoadDhtCache": true,
    "MaxConcurrentFiles": 6,
    "SpeedProbeDurationSeconds": 30
  },
  "GoogleDriveSettings": {
    "ServiceAccountKeyPath": "./service-account.json",
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
| `MaxConcurrentFiles` | Max files downloading simultaneously | `6` |
| `SpeedProbeDurationSeconds` | Duration of initial speed analysis | `30` |
| `ServiceAccountKeyPath` | Google SA key file (primary, VPS) | `./service-account.json` |
| `CredentialsPath` | Google OAuth2 client secret (fallback) | `./credentials.json` |
| `TokenStorePath` | Cached OAuth2 refresh tokens | `./tokens` |
| `TargetFolderId` | Drive folder to upload into (**required** for SA) | `""` |
| `ChunkSizeMB` | Resumable upload chunk size (must be ≥ 8 MB) | `10` |

---

## CLI Architecture

The app uses **System.CommandLine** for structured verb-based CLI parsing, designed for headless VPS operation.

### Command Structure

```
TorrentProject
├── download                  # One-shot: probe → download → upload → delete
│   ├── --torrent <path>      # Path to .torrent file
│   ├── --magnet <link>       # Magnet URI
│   ├── --drive-folder <id>   # Target Google Drive folder ID
│   └── --concurrent <N>      # Override max concurrent files (default: 6)
│
├── daemon                    # Interactive mode: manage multiple torrents
│   (stdin commands)          # add / status / progress / pause / resume / stop / quit
│
├── auth                      # Authenticate with Google (needs browser)
│
└── list-files                # Inspect torrent contents (no download)
    ├── --torrent <path>
    └── --magnet <link>
```

### Daemon Commands

```
> add --magnet "magnet:?xt=..."     Add torrent (auto: probe → download → upload)
> add --torrent "path/file.torrent" Add from .torrent file
> status                            One-line summary per job
> progress <id>                     Per-file detail for one job
> pause <id>                        Pause a running job
> resume <id>                       Resume a paused job
> stop <id>                         Remove a job
> quit                              Graceful shutdown
```

### Command Flow

```
Program.cs
    │
    ├── "download" verb (one-shot)
    │   └── TorrentWorker: probe speeds → concurrent download → upload → delete
    │
    ├── "daemon" verb (interactive, long-running)
    │   ├── DownloadManager: manages List<TorrentJob>
    │   └── CommandHandler: stdin loop → dispatches to DownloadManager
    │
    ├── "auth" verb
    │   └── Opens browser → saves token to ./tokens/
    │
    └── "list-files" verb
        └── Print file list → exit
```

---

## Headless VPS Deployment

### Auth: Service Account (No Browser Needed)

```
┌──────────────────────────────────────┐
│  Google Cloud Console (one-time setup)    │
│                                          │
│  1. Create Service Account                │
│  2. Download JSON key                     │
│  3. Share Drive folder with SA email      │
└──────────────────┬───────────────────┘
                   │ scp service-account.json
                   ▼
┌──────────────────────────────────────┐
│    VPS (headless, no GUI)                 │
│                                          │
│  ./service-account.json  ← SA key file    │
│         │                                 │
│         ▼                                 │
│  GoogleCredential.FromStream()            │
│         │                                 │
│         ▼                                 │
│  DriveService (authenticated)             │
│         │                                 │
│         ▼                                 │
│  Upload to shared Drive folder            │
│  (TargetFolderId in appsettings.json)     │
│                                          │
│  ✔ No browser. No token expiry.            │
│  ✔ Works indefinitely.                     │
└──────────────────────────────────────┘
```

> **Service Account vs OAuth2**: SA credentials use a private key, not tokens.
> There is no refresh cycle — the key works until you delete it in Cloud Console.

### VPS Deployment Architecture

```
/opt/torrentproject/
├── TorrentProject              # Self-contained Linux binary
├── appsettings.json            # Configuration
├── service-account.json        # Google SA key (never expires)
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
ExecStart=/opt/torrentproject/TorrentProject daemon
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
scp ./service-account.json user@vps:/opt/torrentproject/
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

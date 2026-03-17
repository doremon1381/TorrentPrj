# TorrentProject – CLAUDE.md

## Project Overview

A .NET 9 console application that **downloads torrent files and uploads them directly to Google Drive**, optimized for **limited disk space** (30GB HDD).

### Architecture: Speed Probe → Concurrent Download → Upload → Delete

```
.torrent / Magnet Link
        │
        ▼
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│  MonoTorrent     │ ──▶ │  Speed Probe     │ ──▶ │  Concurrent DL  │
│  Engine          │     │  (30s analysis)  │     │  (up to 6 files)│
└─────────────────┘     └──────────────────┘     └────────┬────────┘
                                                          │
                              ┌────────────────────────────┘
                              ▼
                   ┌──────────────┐     ┌─────────────────┐
                   │  Temp Folder  │ ──▶ │  Google Drive   │
                   │  (completed  │     │  Resumable      │
                   │   files)     │     │  Upload         │
                   └──────────────┘     └─────────────────┘
                           │                     │
                           └──── Delete ◄────────┘
                                  │
                                  ▼
                             Next file...
```

**Key constraint**: Only ~30GB of disk space available.
**Strategy**: Probe all files (30s), rank by download speed, then download fastest first with up to 6 concurrent slots.
**Max disk usage** = sum of concurrently active files (not the full torrent).

### Two Operating Modes

- **One-Shot** (`download` verb): Single torrent, download → upload → exit
- **Daemon** (`daemon` verb): Interactive mode, manage multiple torrents with `add/status/progress/pause/resume/stop/quit`

---

## Tech Stack

| Concern              | Package / Technology                         |
| -------------------- | -------------------------------------------- |
| Runtime              | .NET 9                                       |
| Torrent Engine       | `MonoTorrent` (3.x)                          |
| Google Drive         | `Google.Apis.Drive.v3`                       |
| Hosting              | `Microsoft.Extensions.Hosting` (Generic Host)|
| Configuration        | `appsettings.json` + Options pattern         |
| Logging              | `Microsoft.Extensions.Logging` (Console/File)|
| Auth                 | Service Account (VPS) / OAuth2 (local dev)   |

---

## Project Structure

```
TorrentProject/
├── CLAUDE.md                         # This file – project guidelines
├── ARCHITECTURE.md                   # Architecture deep dive
├── REFACTOR.md                       # Clean code standards
├── Configuration/
│   ├── TorrentSettings.cs            # MonoTorrent config + concurrency + probe
│   └── GoogleDriveSettings.cs        # Drive upload config
├── Interfaces/
│   ├── ITorrentService.cs            # Contract: download + probe + concurrent
│   └── IGoogleDriveService.cs        # Contract: upload to Drive
├── Services/
│   ├── TorrentService.cs             # MonoTorrent: load, probe, download
│   ├── GoogleDriveService.cs         # Google Drive resumable upload
│   ├── GoogleAuthService.cs          # Auth: Service Account or OAuth2
│   ├── DownloadManager.cs            # Multi-torrent job orchestrator (daemon)
│   └── CommandHandler.cs             # Interactive stdin command loop (daemon)
├── Workers/
│   └── TorrentWorker.cs              # One-shot download orchestrator
├── Models/
│   ├── DownloadRequest.cs            # CLI args: torrent/magnet/concurrent
│   ├── TorrentMetadata.cs            # Torrent-level metadata
│   ├── TorrentFileInfo.cs            # Per-file metadata (name, size)
│   ├── CompletedFileEvent.cs         # Channel event for completed downloads
│   ├── FileProcessResult.cs          # Per-file result (DL/UL times, Drive ID)
│   └── TorrentJob.cs                 # Per-job state machine (daemon mode)
├── Program.cs                        # CLI: download / daemon / auth / list-files
├── appsettings.json                  # Runtime configuration
└── TorrentProject.csproj
```

---

## Coding Conventions

### General
- Target: **.NET 9**, C# 13, nullable reference types **enabled**
- Use **file-scoped namespaces**
- Use **primary constructors** where appropriate
- Prefer `record` types for immutable data models
- Use `async/await` throughout – avoid `.Result` or `.Wait()`

### Dependency Injection
- Register all services in `Program.cs` using the Generic Host builder
- Use the **Options pattern** (`IOptions<T>`) for configuration
- Depend on **interfaces**, not implementations

### Naming
- Interfaces: `I` prefix (e.g., `ITorrentService`)
- Async methods: `Async` suffix (e.g., `DownloadAsync`)
- Private fields: `_camelCase`
- Configuration sections match class names (e.g., `TorrentSettings` → `"TorrentSettings"` in JSON)

### Error Handling
- Use structured logging with `ILogger<T>`
- Wrap external calls (MonoTorrent, Google APIs) in try-catch with meaningful log messages
- Use `CancellationToken` propagation through all async methods

### Configuration (`appsettings.json`)
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

---

## Key Workflows

### Speed Probe (Pre-Download Analysis)

```
1. Set ALL files to Priority.Normal
2. Start MonoTorrent engine for SpeedProbeDurationSeconds (default: 30)
3. Record bytes received per file
4. Stop engine → rank files by descending speed
5. Return sorted file indices (fastest first)
```

### Concurrent Per-File Processing (Core Algorithm)

```
For up to MaxConcurrentFiles (default: 6) files simultaneously:
  1. Set active files to HIGH priority, queued files to DO_NOT_DOWNLOAD
  2. As each file completes → Channel<CompletedFileEvent>
  3. Upload completed file to Google Drive (resumable, chunked)
  4. Delete local temp file
  5. Activate next file from queue
```

### Daemon Mode Workflow

```
1. User runs: dotnet run -- daemon
2. App starts interactive command loop
3. User adds torrents: add --magnet "..."
4. Each torrent → TorrentJob: probe → download → upload (background)
5. User checks progress on demand: status / progress <id>
6. User can pause/resume/stop individual jobs
7. User exits: quit → graceful shutdown
```

---

## Google Drive Auth Setup (One-Time)

### Method A: Service Account (Recommended for VPS)

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a project → Enable **Google Drive API**
3. **IAM & Admin** → **Service Accounts** → Create Service Account
4. Click the SA → **Keys** tab → **Add Key** → JSON → Download as `service-account.json`
5. **Share your Google Drive folder** with the SA email (e.g., `torrent-uploader@proj.iam.gserviceaccount.com`) as **Editor**
6. Set `TargetFolderId` in `appsettings.json` to that shared folder's ID
7. No browser needed. No token expiry. Works indefinitely.

### Method B: OAuth2 (Local Development Fallback)

1. **Credentials** → Create **OAuth 2.0 Client ID** → Type: **Desktop App**
2. Download JSON → save as `credentials.json` in project root
3. Run `dotnet run -- auth` to authenticate (opens browser)
4. **IMPORTANT**: Publish OAuth consent screen if using longer than 7 days

### Auto-Detection

The app auto-detects which method to use:
- If `service-account.json` exists → use Service Account
- Else if `credentials.json` exists → use OAuth2
- Else → throw error

---

## CLI Commands Reference

### Build & Deploy

```bash
# Restore + build
dotnet restore
dotnet build

# Publish self-contained binary for Linux VPS
dotnet publish -c Release -r linux-x64 --self-contained -o ./publish

# Copy to VPS
scp -r ./publish/ user@vps:/opt/torrentproject/
```

### Core Commands

```bash
# One-shot: download torrent → upload to Drive (probe + concurrent)
dotnet run -- download --torrent "path/to/file.torrent"
dotnet run -- download --magnet "magnet:?xt=urn:btih:..."
dotnet run -- download --torrent "file.torrent" --drive-folder "FolderIdHere"
dotnet run -- download --magnet "..." --concurrent 3

# Interactive daemon mode (manage multiple torrents)
dotnet run -- daemon

# Authenticate with Google (opens browser — run on local machine)
dotnet run -- auth

# List files in a torrent (dry run, no download)
dotnet run -- list-files --torrent "file.torrent"
dotnet run -- list-files --magnet "magnet:?xt=urn:btih:..."
```

### Daemon Commands

```
> add --magnet "magnet:?xt=..."     Add torrent
> add --torrent "file.torrent"      Add from file
> status                            Summary of all jobs
> progress <id>                     Per-file detail
> pause <id>                        Pause a job
> resume <id>                       Resume a job
> stop <id>                         Remove a job
> quit                              Shutdown
```

### On VPS (Published Binary)

```bash
/opt/torrentproject/TorrentProject daemon
/opt/torrentproject/TorrentProject download --magnet "magnet:?xt=urn:btih:..."
/opt/torrentproject/TorrentProject list-files --torrent "/path/to/file.torrent"
```

### Deployment as systemd Service (Linux VPS)

```bash
# Create service file
sudo nano /etc/systemd/system/torrentproject.service
```

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
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
# Enable and start
sudo systemctl daemon-reload
sudo systemctl start torrentproject
sudo systemctl status torrentproject

# View logs
journalctl -u torrentproject -f
```

### Quick Reference

| Command | Description |
|---|---|
| `download --torrent <path>` | One-shot: probe → download → upload to Drive |
| `download --magnet <link>` | Same via magnet link |
| `download ... --concurrent <N>` | Override max concurrent files |
| `daemon` | Interactive mode: manage multiple torrents |
| `auth` | Authenticate with Google (needs browser) |
| `list-files --torrent <path>` | Show files without downloading |

---

## .gitignore Essentials

```
service-account.json
credentials.json
tokens/
temp/
bin/
obj/
```

---

## Future Enhancements

- **Option A migration**: Stream pieces directly to Drive via resumable upload
- **Web UI / API**: ASP.NET minimal API for remote control
- **Notifications**: Email/Telegram alerts on completion
- **Disk space check**: Pre-validate largest file fits in available disk before starting
- **Global concurrency pool**: Shared semaphore across all daemon jobs
- **Auto-retry**: Automatic retry with exponential backoff on upload failures

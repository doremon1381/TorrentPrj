# TorrentProject – CLAUDE.md

## Project Overview

A .NET 9 console application that **downloads torrent files and uploads them directly to Google Drive**, optimized for **limited disk space** (30GB HDD).

### Architecture: Sequential Per-File Download → Upload → Delete → Next File

```
.torrent / Magnet Link
        │
        ▼
┌─────────────────┐     ┌──────────────┐     ┌─────────────────┐
│  MonoTorrent     │ ──▶ │  Temp Folder  │ ──▶ │  Google Drive   │
│  Engine          │     │  (ONE file   │     │  Resumable      │
│  (sequential DL) │     │   at a time) │     │  Upload         │
└─────────────────┘     └──────────────┘     └─────────────────┘
                                │                     │
                                └──── Delete ◄────────┘
                                       │
                                       ▼
                                  Next file...
```

**Key constraint**: Only ~30GB of disk space available.
**Strategy**: Process one file at a time — download, upload to Drive, delete locally, then move to the next file.
**Max disk usage** = size of the largest single file in the torrent (not the full torrent).

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
| Auth                 | OAuth2 User Credentials (Desktop App)        |

---

## Project Structure

```
TorrentProject/
├── CLAUDE.md                         # This file – project guidelines
├── .claude/skills/                   # AI-assistant skills
├── Configuration/
│   ├── TorrentSettings.cs            # MonoTorrent config
│   └── GoogleDriveSettings.cs        # Drive upload config
├── Interfaces/
│   ├── ITorrentService.cs            # Contract: download torrent
│   └── IGoogleDriveService.cs        # Contract: upload to Drive
├── Services/
│   ├── TorrentService.cs             # MonoTorrent download implementation
│   ├── GoogleDriveService.cs         # Google Drive upload implementation
│   └── GoogleAuthService.cs          # OAuth2 token lifecycle
├── Workers/
│   └── TorrentWorker.cs              # BackgroundService orchestrator
├── Models/
│   ├── DownloadRequest.cs            # Input: torrent path/magnet + target folder
│   ├── DownloadResult.cs             # Output: status, file paths
│   └── TorrentFileInfo.cs            # Per-file metadata (name, size, priority)
├── Program.cs                        # Host setup, DI registration
├── appsettings.json                  # Runtime configuration
├── credentials.json                  # Google OAuth2 client secret (git-ignored)
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

---

## Key Workflows

### Per-File Processing Loop (Core Algorithm)

```
For each file in torrent (sorted by index):
  1. Set priority HIGH for this file, LOW for all others
  2. Download this file to temp folder
  3. Upload to Google Drive (resumable, chunked)
  4. Delete local temp file
  5. Move to next file
```

### 1. Download Torrent (Per-File)
1. Parse `.torrent` file or magnet link via MonoTorrent
2. Enumerate all files in the torrent
3. For each file: set it to HIGH priority, set all others to DO_NOT_DOWNLOAD
4. Wait for that file to complete (monitor `file.BitField.PercentComplete`)
5. Report progress via logging (percentage, speed, peers)

### 2. Upload to Google Drive
1. Authenticate via OAuth2 (headless: use `auth` command on a machine with browser first)
2. Create resumable upload session for the downloaded file
3. Upload in chunks (configurable `ChunkSizeMB`)
4. Return the Google Drive file ID on success

### 3. Cleanup & Next
1. Delete the temp file immediately after successful upload
2. Log status (Drive file ID, file size, time taken)
3. Repeat for next file in the torrent

---

## Google Drive Auth Setup (One-Time)

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a project → Enable **Google Drive API**
3. Create **OAuth 2.0 Client ID** → Application type: **Desktop App**
4. Download the JSON → save as `credentials.json` in project root
5. Add `credentials.json` and `tokens/` to `.gitignore`

### Headless VPS Auth (No Browser)

Since VPS has no GUI, authenticate on your **local machine first**, then copy the tokens:

```bash
# Step 1: On your LOCAL machine (has browser)
dotnet run -- auth
# → Opens browser → Grant consent → Token saved to ./tokens/

# Step 2: Copy tokens + credentials to VPS
scp -r ./tokens/ ./credentials.json user@vps:/opt/torrentproject/

# Step 3: On VPS, tokens are reused automatically (refresh token handles renewal)
```

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
# Download torrent → upload to Drive (main workflow)
dotnet run -- download --torrent "path/to/file.torrent"
dotnet run -- download --magnet "magnet:?xt=urn:btih:..."

# Specify target Drive folder
dotnet run -- download --torrent "file.torrent" --drive-folder "FolderIdHere"

# Authenticate with Google (opens browser — run on local machine)
dotnet run -- auth

# List files in a torrent (dry run, no download)
dotnet run -- list-files --torrent "file.torrent"
dotnet run -- list-files --magnet "magnet:?xt=urn:btih:..."
```

### On VPS (Published Binary)

```bash
# Using the published binary directly
/opt/torrentproject/TorrentProject download --torrent "/path/to/file.torrent"
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
ExecStart=/opt/torrentproject/TorrentProject download --torrent "/path/to/file.torrent"
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
| `download --torrent <path>` | Download torrent file → upload to Drive |
| `download --magnet <link>` | Download via magnet link → upload to Drive |
| `download --torrent <path> --drive-folder <id>` | Upload to specific Drive folder |
| `auth` | Authenticate with Google (needs browser) |
| `list-files --torrent <path>` | Show files in torrent without downloading |
| `list-files --magnet <link>` | Show files from magnet link without downloading |

---

## .gitignore Essentials

```
credentials.json
tokens/
temp/
bin/
obj/
```

---

## Future Enhancements (Not in scope for v1)

- **Option A migration**: Stream pieces directly to Drive via resumable upload
- **Web UI / API**: ASP.NET minimal API for remote control
- **Queue system**: Process multiple torrents sequentially
- **Notifications**: Email/Telegram alerts on completion
- **Disk space check**: Pre-validate largest file fits in available disk before starting

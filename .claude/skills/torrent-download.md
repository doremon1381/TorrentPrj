---
name: torrent-download
description: How to implement torrent downloading using MonoTorrent in .NET 9
---

# Torrent Download Skill

## Overview
Implement torrent downloading using the **MonoTorrent** library. This skill covers setting up the engine, loading torrents (file or magnet), starting downloads, tracking progress, and graceful shutdown.

> **Source of truth**: [MonoTorrent official sample – StandardDownloader.cs](https://github.com/alanmcgovern/monotorrent/blob/master/src/Samples/SampleClient/StandardDownloader.cs)

## NuGet Package
```xml
<PackageReference Include="MonoTorrent" Version="3.*" />
```

## Key Classes
- `ClientEngine` – The main torrent engine that manages all torrents
- `TorrentManager` – Manages a single torrent's lifecycle
- `TorrentSettingsBuilder` – Configures per-torrent settings (e.g., `MaximumConnections`)
- `EngineSettingsBuilder` – Configures engine-wide settings (ports, caching, speed limits)

## Implementation Pattern

### 1. Engine Setup
```csharp
using MonoTorrent;
using MonoTorrent.Client;

var settingsBuilder = new EngineSettingsBuilder
{
    // Automatically forward ports via UPnP/NAT-PMP if router supports it
    AllowPortForwarding = true,

    // Auto-save/load DHT cache for faster peer discovery on restart
    AutoSaveLoadDhtCache = true,

    // Auto-save/load FastResume data to skip re-hashing on restart
    AutoSaveLoadFastResume = true,

    // Auto-save/load magnet link metadata so re-downloads skip metadata fetch
    AutoSaveLoadMagnetLinkMetadata = true,

    // Use random ports (0) in production; fixed ports for testing only
    ListenEndPoints = new Dictionary<string, IPEndPoint>
    {
        { "ipv4", new IPEndPoint(IPAddress.Any, 0) },
        { "ipv6", new IPEndPoint(IPAddress.IPv6Any, 0) }
    },

    // DHT endpoint (0 = random port)
    DhtEndPoint = new IPEndPoint(IPAddress.Any, 0)
};

using var engine = new ClientEngine(settingsBuilder.ToSettings());
```

### 2. Loading a .torrent File
```csharp
var torrentSettingsBuilder = new TorrentSettingsBuilder
{
    MaximumConnections = 60
};

// engine.AddAsync accepts a file path directly — no need to call Torrent.LoadAsync first
var manager = await engine.AddAsync(
    "path/to/file.torrent",
    "temp/download/path",
    torrentSettingsBuilder.ToSettings());
```

### 3. Loading a Magnet Link
```csharp
if (MagnetLink.TryParse("magnet:?xt=urn:btih:...", out MagnetLink magnetLink))
{
    var manager = await engine.AddAsync(magnetLink, "temp/download/path");
}
```

### 4. Starting Download & Hooking Events
```csharp
// Hook events before starting
manager.PeersFound += (o, e) =>
    _logger.LogInformation("{Type}: {NewPeers} new peers for {Name}",
        e.GetType().Name, e.NewPeers, e.TorrentManager.Name);

manager.TorrentStateChanged += (o, e) =>
    _logger.LogInformation("State: {OldState} → {NewState}", e.OldState, e.NewState);

manager.PieceHashed += (o, e) =>
    _logger.LogDebug("Piece {Index}: {Result}", e.PieceIndex, e.HashPassed ? "Pass" : "Fail");

// Start download
await manager.StartAsync();
```

### 5. Polling Progress
```csharp
while (manager.State != TorrentState.Stopped && manager.State != TorrentState.Seeding)
{
    var progress = manager.Progress;  // 0.0 – 100.0

    // Use engine-level rates for overall speed
    var downSpeed = engine.TotalDownloadRate / 1024.0;  // KB/s
    var upSpeed = engine.TotalUploadRate / 1024.0;      // KB/s

    // Use manager.Monitor for per-torrent cumulative stats
    var downloaded = manager.Monitor.DataBytesReceived / 1024.0 / 1024.0;  // MB

    // Peers must be retrieved asynchronously
    var peers = await manager.GetPeersAsync();
    var peerCount = peers.Count;

    _logger.LogInformation(
        "Progress: {Progress:F1}% | Speed: {Speed:F1} KB/s | Downloaded: {Downloaded:F1} MB | Peers: {Peers}",
        progress, downSpeed, downloaded, peerCount);

    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
}
```

### 6. Completed File Paths
```csharp
// After download completes (state = Seeding), get all file paths
foreach (var file in manager.Files)
{
    var fullPath = file.FullPath;
    var size = file.Length;
    var percentComplete = file.BitField.PercentComplete;
    _logger.LogInformation("Downloaded: {Path} ({Size} bytes, {Pct:F1}%)",
        fullPath, size, percentComplete);
}
```

### 7. Graceful Shutdown
```csharp
// Stop each manager individually, then wait for it to reach Stopped state
var stoppingTask = manager.StopAsync();
while (manager.State != TorrentState.Stopped)
{
    _logger.LogInformation("{Name} is {State}", manager.Torrent?.Name, manager.State);
    await Task.WhenAll(stoppingTask, Task.Delay(250));
}
await stoppingTask;

// If AutoSaveLoadFastResume is enabled, fast resume data is written automatically
// If AutoSaveLoadDhtCache is enabled, DHT cache is saved automatically
```

## Interface Contract
```csharp
public interface ITorrentService
{
    Task<DownloadResult> DownloadAsync(
        string torrentPathOrMagnet,
        string downloadDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## Error Handling
- Wrap `engine.AddAsync` in try-catch for invalid torrent files (the exception message will explain the issue)
- Handle `OperationCanceledException` for user-initiated cancellation via `CancellationToken`
- Hook into `manager.PieceHashed` event to log piece-hash failures
- For magnet links, monitor `manager.State` — it stays in `Metadata` state until torrent info is downloaded
- Use `Console.CancelKeyPress` / `AppDomain.CurrentDomain.ProcessExit` for clean shutdown on Ctrl+C

## Tips
- Use `engine.AddAsync(filePath, ...)` directly — it handles loading the `.torrent` internally
- Use `MagnetLink.TryParse()` (not `Parse()`) to safely handle invalid magnet URIs
- Use `TorrentState.Seeding` to detect download completion (not `Stopped`)
- For magnet links, metadata download can take time — log `TorrentStateChanged` events
- Always stop each `TorrentManager` individually with `StopAsync()` and wait for `TorrentState.Stopped`
- Enable `AutoSaveLoadFastResume = true` so re-starts skip the hash-check phase
- Use random ports (`0`) in production for `ListenEndPoints` and `DhtEndPoint`
- `manager.Monitor.DataBytesReceived` gives cumulative bytes; `engine.TotalDownloadRate` gives current rate

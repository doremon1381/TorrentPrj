---
name: worker-orchestration
description: How to orchestrate the download-then-upload workflow using .NET Generic Host and BackgroundService
---

# Worker Orchestration Skill

## Overview
Orchestrate the **Download → Upload → Cleanup** pipeline using `Microsoft.Extensions.Hosting` and `BackgroundService`. This skill covers host setup, DI registration, configuration binding, and the worker lifecycle.

## NuGet Packages
```xml
<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.*" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="9.*" />
```

## Program.cs – Host Setup
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TorrentProject.Configuration;
using TorrentProject.Interfaces;
using TorrentProject.Services;
using TorrentProject.Workers;

var builder = Host.CreateApplicationBuilder(args);

// Bind configuration sections
builder.Services.Configure<TorrentSettings>(
    builder.Configuration.GetSection(nameof(TorrentSettings)));
builder.Services.Configure<GoogleDriveSettings>(
    builder.Configuration.GetSection(nameof(GoogleDriveSettings)));

// Register services
builder.Services.AddSingleton<GoogleAuthService>();
builder.Services.AddSingleton<ITorrentService, TorrentService>();
builder.Services.AddSingleton<IGoogleDriveService, GoogleDriveService>();

// Register background worker
builder.Services.AddHostedService<TorrentWorker>();

var host = builder.Build();
await host.RunAsync();
```

## Configuration Classes
```csharp
// Configuration/TorrentSettings.cs
public record TorrentSettings
{
    public string TempDownloadPath { get; init; } = "./temp";
    public int MaxConnections { get; init; } = 60;
    public bool AllowPortForwarding { get; init; } = true;
    public bool AutoSaveLoadFastResume { get; init; } = true;
    public bool AutoSaveLoadDhtCache { get; init; } = true;
}

// Configuration/GoogleDriveSettings.cs
public record GoogleDriveSettings
{
    public string CredentialsPath { get; init; } = "./credentials.json";
    public string TokenStorePath { get; init; } = "./tokens";
    public string TargetFolderId { get; init; } = "";
    public int ChunkSizeMB { get; init; } = 10;
}
```

## TorrentWorker – BackgroundService Pattern
```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class TorrentWorker(
    ITorrentService torrentService,
    IGoogleDriveService driveService,
    IOptions<TorrentSettings> torrentSettings,
    IOptions<GoogleDriveSettings> driveSettings,
    ILogger<TorrentWorker> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 1. Parse input (torrent path or magnet from command-line args)
            var input = GetInputFromArgs();

            // 2. Download
            logger.LogInformation("Starting download: {Input}", input);
            var result = await torrentService.DownloadAsync(
                input,
                torrentSettings.Value.TempDownloadPath,
                cancellationToken: stoppingToken);

            // 3. Upload each downloaded file
            foreach (var filePath in result.FilePaths)
            {
                logger.LogInformation("Uploading to Drive: {File}", filePath);
                var driveId = await driveService.UploadFileAsync(
                    filePath,
                    driveSettings.Value.TargetFolderId,
                    cancellationToken: stoppingToken);

                logger.LogInformation("Uploaded! Drive ID: {DriveId}", driveId);
            }

            // 4. Cleanup temp files
            CleanupTempFiles(result.FilePaths);

            logger.LogInformation("All done! Shutting down.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Operation was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error in torrent worker.");
        }
        finally
        {
            // Stop the host after the work is done
            lifetime.StopApplication();
        }
    }

    private void CleanupTempFiles(IEnumerable<string> filePaths)
    {
        foreach (var file in filePaths)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    logger.LogInformation("Cleaned up: {File}", file);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete temp file: {File}", file);
            }
        }
    }

    private string GetInputFromArgs()
    {
        // Environment.GetCommandLineArgs()[0] is the executable path,
        // so user arguments start at index 1
        var args = Environment.GetCommandLineArgs();
        var (type, value) = ParseArgs(args);
        return value;
    }
}
```

## Lifecycle Flow
```
Host.RunAsync()
    │
    ├── TorrentWorker.ExecuteAsync() starts
    │       │
    │       ├── Download torrent → temp folder
    │       ├── Upload file(s) → Google Drive
    │       ├── Delete temp file(s)
    │       └── Call lifetime.StopApplication()
    │
    └── Host shuts down gracefully
```

## Command-Line Argument Parsing
```csharp
// Simple approach using args array
private static (string Type, string Value) ParseArgs(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--torrent") return ("torrent", args[i + 1]);
        if (args[i] == "--magnet") return ("magnet", args[i + 1]);
    }
    throw new ArgumentException("Usage: --torrent <path> or --magnet <link>");
}

// Or use System.CommandLine for more robust parsing:
// <PackageReference Include="System.CommandLine" Version="2.*" />
```

## Tips
- Use `IHostApplicationLifetime.StopApplication()` to shut down the host after work is done
- Register services as `Singleton` since there's only one worker
- Use `IOptions<T>` (not `IOptionsSnapshot<T>`) since config doesn't change at runtime
- Always propagate `stoppingToken` to all async calls
- Log structured data with templates (`{Property}`), not string interpolation

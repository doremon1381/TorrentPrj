using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TorrentProject.Configuration;
using TorrentProject.Interfaces;
using TorrentProject.Models;
using TorrentProject.Services;
using TorrentProject.Workers;

// ─────────────────────────────────────────────────────────────
//  TorrentProject CLI
//  Verbs: download | auth | list-files
// ─────────────────────────────────────────────────────────────

var rootCommand = new RootCommand("TorrentProject – Download torrents to Google Drive");

// ── download ──────────────────────────────────────────────────
var downloadCommand = new Command("download", "Download torrent → upload to Google Drive → delete local");

var torrentOption = new Option<string?>("--torrent") { Description = "Path to a .torrent file" };
var magnetOption = new Option<string?>("--magnet") { Description = "Magnet URI" };
var driveFolderOption = new Option<string?>("--drive-folder") { Description = "Target Google Drive folder ID" };
var concurrentOption = new Option<int?>("--concurrent") { Description = "Max concurrent file downloads (default: 6)" };

downloadCommand.Options.Add(torrentOption);
downloadCommand.Options.Add(magnetOption);
downloadCommand.Options.Add(driveFolderOption);
downloadCommand.Options.Add(concurrentOption);

downloadCommand.SetAction(async (parseResult, ct) =>
{
    var torrentPath = parseResult.GetValue(torrentOption);
    var magnet = parseResult.GetValue(magnetOption);
    var driveFolderId = parseResult.GetValue(driveFolderOption);
    var concurrent = parseResult.GetValue(concurrentOption);

    if (string.IsNullOrEmpty(torrentPath) && string.IsNullOrEmpty(magnet))
    {
        Console.Error.WriteLine("Error: Provide either --torrent <path> or --magnet <link>");
        return;
    }

    var request = new DownloadRequest
    {
        TorrentPath = torrentPath,
        Magnet = magnet,
        DriveFolderId = driveFolderId,
        MaxConcurrentFiles = concurrent
    };

    var builder = Host.CreateApplicationBuilder();

    // Bind configuration
    builder.Services.Configure<TorrentSettings>(
        builder.Configuration.GetSection(nameof(TorrentSettings)));
    builder.Services.Configure<GoogleDriveSettings>(
        builder.Configuration.GetSection(nameof(GoogleDriveSettings)));

    // Register services
    builder.Services.AddSingleton(request);
    builder.Services.AddSingleton<GoogleAuthService>();
    builder.Services.AddSingleton<ITorrentService, TorrentService>();
    builder.Services.AddSingleton<IGoogleDriveService, GoogleDriveService>();

    // Register background worker
    builder.Services.AddHostedService<TorrentWorker>();

    var host = builder.Build();
    await host.RunAsync(ct);
});

rootCommand.Subcommands.Add(downloadCommand);

// ── auth ──────────────────────────────────────────────────────
var authCommand = new Command("auth", "Authenticate with Google (opens browser for OAuth2)");

authCommand.SetAction(async (parseResult, ct) =>
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.Configure<GoogleDriveSettings>(
        builder.Configuration.GetSection(nameof(GoogleDriveSettings)));

    var host = builder.Build();
    var settings = host.Services.GetRequiredService<IOptions<GoogleDriveSettings>>().Value;
    var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("Auth");

    if (!File.Exists(settings.CredentialsPath))
    {
        Console.Error.WriteLine($"Error: '{settings.CredentialsPath}' not found.");
        Console.Error.WriteLine("Download OAuth2 client credentials from Google Cloud Console.");
        return;
    }

    logger.LogInformation("Starting OAuth2 authentication (this will open your browser)...");

    var authService = new GoogleAuthService(loggerFactory.CreateLogger<GoogleAuthService>());

    await authService.AuthenticateWithOAuth2Async(
        settings.CredentialsPath, settings.TokenStorePath, ct);

    logger.LogInformation("Authentication successful! Tokens saved to: {Path}", settings.TokenStorePath);
    logger.LogInformation("You can now copy the '{Path}' folder to your VPS.", settings.TokenStorePath);
});

rootCommand.Subcommands.Add(authCommand);

// ── upload ────────────────────────────────────────────────────
var uploadCommand = new Command("upload", "Upload all files from temp folder to Google Drive (recovery after failed uploads)");

var uploadPathOption = new Option<string?>("--path") { Description = "Folder to scan for files (defaults to TempDownloadPath from appsettings.json)" };
var uploadDriveFolderOption = new Option<string?>("--drive-folder") { Description = "Target Google Drive folder ID (overrides appsettings.json)" };
var uploadDeleteOption = new Option<bool>("--delete") { Description = "Delete local files after successful upload", DefaultValueFactory = _ => false };

uploadCommand.Options.Add(uploadPathOption);
uploadCommand.Options.Add(uploadDriveFolderOption);
uploadCommand.Options.Add(uploadDeleteOption);

uploadCommand.SetAction(async (parseResult, ct) =>
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.Configure<TorrentSettings>(
        builder.Configuration.GetSection(nameof(TorrentSettings)));
    builder.Services.Configure<GoogleDriveSettings>(
        builder.Configuration.GetSection(nameof(GoogleDriveSettings)));

    var host = builder.Build();
    var torrentSettings = host.Services.GetRequiredService<IOptions<TorrentSettings>>().Value;
    var driveSettings = host.Services.GetRequiredService<IOptions<GoogleDriveSettings>>().Value;
    var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("Upload");

    var folder = parseResult.GetValue(uploadPathOption) ?? torrentSettings.TempDownloadPath;
    var driveFolderId = parseResult.GetValue(uploadDriveFolderOption) ?? driveSettings.TargetFolderId;
    var deleteAfterUpload = parseResult.GetValue(uploadDeleteOption);

    folder = Path.GetFullPath(folder);

    if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"Error: Folder not found: {folder}");
        return;
    }

    var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);

    if (files.Length == 0)
    {
        Console.Error.WriteLine($"No files found in: {folder}");
        return;
    }

    logger.LogInformation("Found {Count} file(s) in {Folder}", files.Length, folder);

    foreach (var file in files)
    {
        var size = new FileInfo(file).Length;
        logger.LogInformation("  {File} ({Size:F2} MB)", Path.GetFileName(file), size / 1024.0 / 1024.0);
    }

    var authService = new GoogleAuthService(loggerFactory.CreateLogger<GoogleAuthService>());
    var driveService = new GoogleDriveService(authService, Options.Create(driveSettings), loggerFactory.CreateLogger<GoogleDriveService>());

    var successCount = 0;
    var failCount = 0;

    foreach (var file in files)
    {
        try
        {
            logger.LogInformation("Uploading: {File}", Path.GetFileName(file));
            var driveFileId = await driveService.UploadFileAsync(
                file,
                string.IsNullOrEmpty(driveFolderId) ? null : driveFolderId,
                ct: ct);

            logger.LogInformation("✓ Uploaded: {File} → Drive ID: {Id}", Path.GetFileName(file), driveFileId);
            successCount++;

            if (deleteAfterUpload)
            {
                File.Delete(file);
                logger.LogInformation("  Deleted: {File}", file);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "✗ Failed to upload: {File}", Path.GetFileName(file));
            failCount++;
        }
    }

    logger.LogInformation("Done! {Success} uploaded, {Failed} failed", successCount, failCount);
});

rootCommand.Subcommands.Add(uploadCommand);

// ── list-files ────────────────────────────────────────────────
var listFilesCommand = new Command("list-files", "Inspect torrent contents without downloading");

var listTorrentOption = new Option<string?>("--torrent") { Description = "Path to a .torrent file" };
var listMagnetOption = new Option<string?>("--magnet") { Description = "Magnet URI" };

listFilesCommand.Options.Add(listTorrentOption);
listFilesCommand.Options.Add(listMagnetOption);

listFilesCommand.SetAction(async (parseResult, ct) =>
{
    var torrentPath = parseResult.GetValue(listTorrentOption);
    var magnet = parseResult.GetValue(listMagnetOption);

    if (string.IsNullOrEmpty(torrentPath) && string.IsNullOrEmpty(magnet))
    {
        Console.Error.WriteLine("Error: Provide either --torrent <path> or --magnet <link>");
        return;
    }

    var input = torrentPath ?? magnet!;

    var builder = Host.CreateApplicationBuilder();
    builder.Services.Configure<TorrentSettings>(
        builder.Configuration.GetSection(nameof(TorrentSettings)));

    var host = builder.Build();
    var settings = host.Services.GetRequiredService<IOptions<TorrentSettings>>().Value;
    var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

    var torrentService = new TorrentService(
        Options.Create(settings),
        loggerFactory.CreateLogger<TorrentService>());

    try
    {
        var metadata = await torrentService.LoadTorrentAsync(input, ct);

        Console.WriteLine();
        Console.WriteLine($"  Torrent: {metadata.Name}");
        Console.WriteLine($"  Files:   {metadata.Files.Count}");
        Console.WriteLine($"  Total:   {metadata.TotalSize / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine();

        Console.WriteLine("  # │ Size (MB)  │ File");
        Console.WriteLine("  ──┼────────────┼──────────────────────────────────");

        foreach (var file in metadata.Files)
        {
            Console.WriteLine(
                $"  {file.Index,2} │ {file.Size / 1024.0 / 1024.0,10:F2} │ {file.Path}");
        }

        Console.WriteLine();
    }
    finally
    {
        await torrentService.StopAsync();
        torrentService.Dispose();
    }
});

rootCommand.Subcommands.Add(listFilesCommand);

// ── Run ───────────────────────────────────────────────────────
return await rootCommand.Parse(args).InvokeAsync();

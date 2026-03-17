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

var torrentOption = new Option<string?>("--torrent", "Path to a .torrent file");
var magnetOption = new Option<string?>("--magnet", "Magnet URI");
var driveFolderOption = new Option<string?>("--drive-folder", "Target Google Drive folder ID");

downloadCommand.Options.Add(torrentOption);
downloadCommand.Options.Add(magnetOption);
downloadCommand.Options.Add(driveFolderOption);

downloadCommand.SetAction(async (parseResult, ct) =>
{
    var torrentPath = parseResult.GetValue(torrentOption);
    var magnet = parseResult.GetValue(magnetOption);
    var driveFolderId = parseResult.GetValue(driveFolderOption);

    if (string.IsNullOrEmpty(torrentPath) && string.IsNullOrEmpty(magnet))
    {
        Console.Error.WriteLine("Error: Provide either --torrent <path> or --magnet <link>");
        return;
    }

    var request = new DownloadRequest
    {
        TorrentPath = torrentPath,
        Magnet = magnet,
        DriveFolderId = driveFolderId
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

// ── list-files ────────────────────────────────────────────────
var listFilesCommand = new Command("list-files", "Inspect torrent contents without downloading");

var listTorrentOption = new Option<string?>("--torrent", "Path to a .torrent file");
var listMagnetOption = new Option<string?>("--magnet", "Magnet URI");

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

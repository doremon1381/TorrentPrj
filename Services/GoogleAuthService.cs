using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Logging;
using TorrentProject.Configuration;

namespace TorrentProject.Services;

/// <summary>
/// Handles Google authentication. Auto-detects between Service Account (headless VPS)
/// and OAuth2 user credentials (local dev with browser).
/// </summary>
public sealed class GoogleAuthService(ILogger<GoogleAuthService> logger)
{
    private const string ApplicationName = "TorrentProject";
    private static readonly string[] Scopes = [DriveService.Scope.DriveFile];

    /// <summary>
    /// Auto-detect auth method: Service Account key → OAuth2 → error.
    /// </summary>
    public async Task<DriveService> AuthenticateAsync(
        GoogleDriveSettings settings,
        CancellationToken ct = default)
    {
        if (File.Exists(settings.ServiceAccountKeyPath))
        {
            logger.LogInformation("Using Service Account auth: {Path}", settings.ServiceAccountKeyPath);
            return await AuthenticateWithServiceAccountAsync(settings.ServiceAccountKeyPath, ct);
        }

        if (File.Exists(settings.CredentialsPath))
        {
            logger.LogInformation("Using OAuth2 auth: {Path}", settings.CredentialsPath);
            return await AuthenticateWithOAuth2Async(settings.CredentialsPath, settings.TokenStorePath, ct);
        }

        throw new FileNotFoundException(
            $"No auth credentials found. Provide either " +
            $"'{settings.ServiceAccountKeyPath}' (Service Account) or " +
            $"'{settings.CredentialsPath}' (OAuth2).");
    }

    /// <summary>
    /// Authenticate using Service Account key (headless, no browser needed).
    /// </summary>
    private async Task<DriveService> AuthenticateWithServiceAccountAsync(
        string keyPath, CancellationToken ct)
    {
        await using var stream = new FileStream(keyPath, FileMode.Open, FileAccess.Read);
#pragma warning disable CS0618 // GoogleCredential.FromStream is deprecated but CredentialFactory replacement requires additional setup
        var credential = GoogleCredential.FromStream(stream)
            .CreateScoped(Scopes);
#pragma warning restore CS0618

        logger.LogInformation("Service Account authenticated successfully");

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
    }

    /// <summary>
    /// Authenticate using OAuth2 user credentials (opens browser on first run).
    /// </summary>
    public async Task<DriveService> AuthenticateWithOAuth2Async(
        string credentialsPath, string tokenStorePath, CancellationToken ct)
    {
        await using var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            (await GoogleClientSecrets.FromStreamAsync(stream, ct)).Secrets,
            Scopes, "user", ct,
            new FileDataStore(tokenStorePath, true));

        logger.LogInformation("OAuth2 authenticated successfully");

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
    }
}

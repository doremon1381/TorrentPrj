namespace TorrentProject.Configuration;

/// <summary>
/// Configuration for the MonoTorrent download engine.
/// Bound from the "TorrentSettings" section of appsettings.json.
/// </summary>
public sealed record TorrentSettings
{
    /// <summary>Where MonoTorrent writes downloaded files before upload.</summary>
    public string TempDownloadPath { get; init; } = "./temp";

    /// <summary>Maximum peer connections per torrent.</summary>
    public int MaxConnections { get; init; } = 60;

    /// <summary>Enable UPnP/NAT-PMP port forwarding for better connectivity.</summary>
    public bool AllowPortForwarding { get; init; } = true;

    /// <summary>Persist fast-resume data to skip re-hashing on restart.</summary>
    public bool AutoSaveLoadFastResume { get; init; } = true;

    /// <summary>Persist DHT cache for faster peer discovery on restart.</summary>
    public bool AutoSaveLoadDhtCache { get; init; } = true;

    /// <summary>Maximum number of files to download concurrently within a torrent.</summary>
    public int MaxConcurrentFiles { get; init; } = 6;

    /// <summary>Duration in seconds to probe file download speeds before prioritizing.</summary>
    public int SpeedProbeDurationSeconds { get; init; } = 30;
}

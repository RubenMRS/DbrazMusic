namespace MusicBot.Audio;

public sealed record TrackInfo(
    string Title,
    string Url,
    TimeSpan? Duration,
    string? Uploader,
    string? Thumbnail,
    bool IsLive)
{
    public string DurationText => IsLive || Duration is null
        ? "direto 🔴"
        : Duration.Value.TotalHours >= 1
            ? Duration.Value.ToString(@"h\:mm\:ss")
            : Duration.Value.ToString(@"m\:ss");
}

public sealed record PlaybackItem(
    TrackInfo Track,
    ulong RequestedBy,
    string RequestedByName,
    ulong TextChannelId);

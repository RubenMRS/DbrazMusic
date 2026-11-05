using System.Text.Json;

namespace MusicBot;

public sealed class BotConfig
{
    public string Token { get; set; } = "";
    public ulong GuildId { get; set; }

    // No Windows usamos os binários locais em tools\; no Linux (Docker) vêm do PATH.
    public string YtDlpPath { get; set; } = OperatingSystem.IsWindows() ? "tools/yt-dlp.exe" : "yt-dlp";
    public string FfmpegPath { get; set; } = OperatingSystem.IsWindows() ? "tools/ffmpeg.exe" : "ffmpeg";

    public string CookiesFile { get; set; } = "";
    public int MaxQueueSize { get; set; } = 50;
    public int IdleLeaveMinutes { get; set; } = 5;
    public bool AnnounceNowPlaying { get; set; } = true;
    public string[] ExtraYtDlpArgs { get; set; } = [];
    public string[] ExtraFfmpegArgs { get; set; } = [];

    public string? ResolvedYtDlp { get; private set; }
    public string? ResolvedFfmpeg { get; private set; }
    public string? ResolvedCookies { get; private set; }

    /// <summary>
    /// Lê o config.json. Se o ficheiro não existir (por exemplo no Render),
    /// usa os valores por omissão e as variáveis de ambiente.
    /// </summary>
    public static BotConfig Load(string? path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        BotConfig config;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            config = new BotConfig();
        }
        else
        {
            config = JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(path), options)
                     ?? new BotConfig();
        }

        ApplyEnvironment(config);

        config.ResolvedYtDlp = ToolLocator.Resolve(config.YtDlpPath, "yt-dlp.exe");
        config.ResolvedFfmpeg = ToolLocator.Resolve(config.FfmpegPath, "ffmpeg.exe");
        config.ResolvedCookies = ToolLocator.ResolveOptional(config.CookiesFile);
        config.ResolvedCookies ??= FindSecretCookies();

        return config;
    }

    /// <summary>
    /// No Render, os Secret Files aparecem em /etc/secrets/. Se não houver caminho
    /// configurado, usa o cookies.txt que lá estiver.
    /// </summary>
    private static string? FindSecretCookies()
    {
        foreach (var candidate in SecretCookieCandidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Caminho inválido neste sistema - ignora.
            }
        }

        return null;
    }

    public static IReadOnlyList<string> SecretCookieCandidates { get; } =
    [
        "/etc/secrets/cookies.txt",
        "/etc/secrets/cookies",
        "/run/secrets/cookies.txt",
    ];

    /// <summary>Variáveis de ambiente ganham ao config.json (útil na nuvem).</summary>
    private static void ApplyEnvironment(BotConfig config)
    {
        if (Read("YTDLP_PATH") is { } ytDlp)
            config.YtDlpPath = ytDlp;

        if (Read("FFMPEG_PATH") is { } ffmpeg)
            config.FfmpegPath = ffmpeg;

        if (Read("COOKIES_FILE") is { } cookies)
            config.CookiesFile = cookies;

        var guild = Read("DISCORD_GUILD_ID") ?? Read("GUILD_ID");
        if (guild != null && ulong.TryParse(guild, out var guildId))
            config.GuildId = guildId;
    }

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public string EffectiveToken =>
        Read("DISCORD_TOKEN") ?? Token;
}

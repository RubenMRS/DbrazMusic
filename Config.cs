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
    /// Fica a true quando o YouTube rejeita a sessão dos cookies (o Google roda-os
    /// periodicamente). A partir daí o bot usa só o PO token.
    /// </summary>
    public bool CookiesDisabled { get; set; }

    public bool UseCookies => !CookiesDisabled && ResolvedCookies != null;

    /// <summary>Caminho dos cookies a passar ao yt-dlp, ou null se estiverem desativados.</summary>
    public string? CookiesToPass => UseCookies ? ResolvedCookies : null;

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
        config.ResolvedCookies = MakeWritableCookieCopy(config.ResolvedCookies ?? FindSecretCookies());

        return config;
    }

    /// <summary>
    /// O yt-dlp abre o ficheiro de cookies em escrita para guardar rotações, e no Render
    /// /etc/secrets é só de leitura. Se não der para escrever, copia-o para uma pasta temporária.
    /// </summary>
    private static string? MakeWritableCookieCopy(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var probe = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return path;
        }
        catch
        {
            try
            {
                var target = Path.Combine(Path.GetTempPath(), "cookies.txt");
                File.Copy(path, target, overwrite: true);
                return target;
            }
            catch
            {
                // Se nem copiar der, usa o original (pode falhar no yt-dlp, mas não é pior).
                return path;
            }
        }
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

        // YTDLP_COOKIES=0 desliga os cookies (útil quando o Google já os rodou).
        if (Read("YTDLP_COOKIES") is { } cookiesFlag
            && cookiesFlag is "0" or "false" or "no" or "off")
        {
            config.CookiesDisabled = true;
        }

        // Permite afinar o yt-dlp na nuvem sem rebuild:
        //   YTDLP_ARGS=--extractor-args youtube:player_client=mweb,tv
        if (Read("YTDLP_ARGS") is { } extra)
            config.ExtraYtDlpArgs = SplitArgs(extra);

        var guild = Read("DISCORD_GUILD_ID") ?? Read("GUILD_ID");
        if (guild != null && ulong.TryParse(guild, out var guildId))
            config.GuildId = guildId;
    }

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Divide uma linha de argumentos, respeitando aspas simples e duplas.</summary>
    private static string[] SplitArgs(string line)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var c in line)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return [.. args];
    }

    public string EffectiveToken =>
        Read("DISCORD_TOKEN") ?? Token;
}

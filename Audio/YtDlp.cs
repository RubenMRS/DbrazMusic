using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MusicBot.Audio;

public static class YtDlp
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Resolve um link ou uma pesquisa em informação da música (sem descarregar áudio).
    /// </summary>
    public static async Task<(TrackInfo? Info, string? Error)> ResolveAsync(
        string query, BotConfig config, CancellationToken cancellationToken = default)
    {
        if (config.ResolvedYtDlp is not { } ytDlp)
            return (null, "yt-dlp não encontrado. Corre o setup.ps1 ou define o caminho no config.json.");

        var isUrl = Uri.TryCreate(query, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        var target = isUrl ? query : $"ytsearch1:{query}";

        var psi = new ProcessStartInfo
        {
            FileName = ytDlp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in BuildBaseArgs(config))
            psi.ArgumentList.Add(arg);

        psi.ArgumentList.Add("--dump-json");
        psi.ArgumentList.Add("--skip-download");
        // Verbose só nos metadados: é o que mostra o estado dos PO tokens e do cliente usado.
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add(target);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (null, $"Não consegui executar o yt-dlp: {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return (null, "O YouTube demorou demasiado a responder. Tenta outra vez.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var diagnostics = Diagnostics(stderr);
            if (diagnostics.Length > 0)
                Log.Warn($"Diagnóstico do yt-dlp:\n{diagnostics}");

            return (null, ShortError(stderr));
        }

        var jsonLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith('{'));

        if (jsonLine is null)
            return (null, "Não encontrei resultados para essa pesquisa.");

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            var title = GetString(root, "title") ?? "Sem título";
            var url = GetString(root, "webpage_url") ?? GetString(root, "url") ?? target;
            var uploader = GetString(root, "uploader")
                           ?? GetString(root, "channel")
                           ?? GetString(root, "artist");
            var thumbnail = GetString(root, "thumbnail");
            var isLive = root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True;
            var duration = GetDuration(root);

            return (new TrackInfo(title, url, duration, uploader, thumbnail, isLive), null);
        }
        catch (JsonException)
        {
            return (null, "Recebi uma resposta inválida do yt-dlp.");
        }
    }

    public static IEnumerable<string> BuildBaseArgs(BotConfig config)
    {
        yield return "--ignore-config";
        yield return "--no-warnings";
        yield return "--no-playlist";

        if (config.ResolvedCookies is { } cookies)
        {
            yield return "--cookies";
            yield return cookies;
        }

        // O yt-dlp precisa de um runtime JS (deno) para desbloquear alguns formatos do YouTube.
        if (FindDeno(config.ResolvedYtDlp) is { } deno)
        {
            yield return "--js-runtimes";
            yield return $"deno:{deno}";
        }

        foreach (var extra in config.ExtraYtDlpArgs)
            yield return extra;
    }

    private static string? FindDeno(string? ytDlpPath)
    {
        if (string.IsNullOrWhiteSpace(ytDlpPath))
            return null;

        var dir = Path.GetDirectoryName(ytDlpPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        foreach (var name in new[] { "deno.exe", "deno" })
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static TimeSpan? GetDuration(JsonElement root)
    {
        if (!root.TryGetProperty("duration", out var duration)) return null;
        return duration.ValueKind switch
        {
            JsonValueKind.Number when duration.TryGetDouble(out var seconds) && seconds > 0
                => TimeSpan.FromSeconds(seconds),
            _ => null,
        };
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Filtra o stderr verboso do yt-dlp para as linhas que interessam quando o
    /// YouTube bloqueia (PO token, cliente usado, cookies, erro final).
    /// </summary>
    private static string Diagnostics(string stderr)
    {
        string[] keywords =
        [
            "[pot", "po token", "po_token", "sign in", "player_client", "player client",
            "[client", "cookie", "error", "warning", "bgutil", "visitor",
        ];

        var lines = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Where(l => keywords.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .TakeLast(30)
            .ToArray();

        var text = string.Join("\n", lines);
        return text.Length > 4000 ? text[^4000..] : text;
    }

    private static string ShortError(string stderr)
    {
        var lines = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .TakeLast(3)
            .ToArray();

        return lines.Length > 0
            ? string.Join(" | ", lines).Replace("ERROR: ", "", StringComparison.OrdinalIgnoreCase)
            : "Erro desconhecido no yt-dlp.";
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* ignore */ }
    }
}

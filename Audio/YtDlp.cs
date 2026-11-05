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
            return (null, ShortError(stderr));

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

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MusicBot.Audio;

public static class YtDlp
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan TotalResolveTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Clientes do YouTube a experimentar quando o habitual é bloqueado.</summary>
    private static readonly string[] FallbackClients = ["web_embedded", "mweb", "android", "tv"];

    /// <summary>
    /// Resolve um link ou uma pesquisa em informação da música (sem descarregar áudio).
    /// Se o YouTube bloquear, tenta sem cookies e depois com outros clientes.
    /// <paramref name="progress"/> recebe um aviso quando a primeira tentativa falha
    /// (para o utilizador não ficar a olhar para "a pensar...").
    /// </summary>
    public static async Task<(TrackInfo? Info, string? Error)> ResolveAsync(
        string query, BotConfig config, Func<string, Task>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (config.ResolvedYtDlp is not { } ytDlp)
            return (null, "yt-dlp não encontrado. Corre o setup.ps1 ou define o caminho no config.json.");

        var isUrl = Uri.TryCreate(query, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        var target = isUrl ? query : $"ytsearch1:{query}";

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TotalResolveTimeout);

        var result = await ResolveOnceAsync(ytDlp, target, config, config.PlayerClient, deadline.Token);
        if (result.Info != null || !IsSessionRejected(result.Error))
            return result;

        if (progress != null)
            await progress("⏳ O YouTube bloqueou o pedido. A tentar alternativas (pode demorar ~1 min)…");

        // 2) Cookies inválidos (o Google roda-os) fazem o YouTube tratar o pedido
        //    como suspeito. Sem eles, o PO token do bgutil é o suficiente.
        if (config.UseCookies)
        {
            Log.Warn("O YouTube rejeitou a sessão dos cookies; a tentar sem cookies.");
            config.CookiesDisabled = true;

            result = await ResolveOnceAsync(ytDlp, target, config, config.PlayerClient, deadline.Token);
            if (result.Info != null)
                return result;
        }

        // 3) Cada cliente do YouTube é pontuado de forma diferente pelo anti-bot.
        foreach (var client in FallbackClients)
        {
            if (client == config.PlayerClient)
                continue;

            Log.Info($"A tentar o cliente do YouTube \"{client}\"...");

            var attempt = await ResolveOnceAsync(ytDlp, target, config, client, deadline.Token);
            if (attempt.Info != null)
            {
                config.PlayerClient = client;
                Log.Ok($"Cliente do YouTube que funcionou: {client}");
                return attempt;
            }

            if (!IsSessionRejected(attempt.Error))
                return attempt;

            result = attempt;
        }

        return (null, result.Error);
    }

    /// <summary>Sinais de que o problema é o bloqueio do YouTube, não o vídeo.</summary>
    private static bool IsSessionRejected(string? error)
        => error != null
           && (error.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
               || error.Contains("cookies are no longer valid", StringComparison.OrdinalIgnoreCase)
               || error.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase)
               || error.Contains("not a bot", StringComparison.OrdinalIgnoreCase));

    private static async Task<(TrackInfo? Info, string? Error)> ResolveOnceAsync(
        string ytDlp, string target, BotConfig config, string? client, CancellationToken cancellationToken)
    {
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

        if (client != null && client != config.PlayerClient)
        {
            psi.ArgumentList.Add("--extractor-args");
            psi.ArgumentList.Add($"youtube:player_client={client}");
        }

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

            var message = ShortError(stderr);
            if (diagnostics.Length > 0)
                message += $"\n```\n{Trim(diagnostics, 900)}\n```";

            return (null, message);
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

        // IPv6 de datacenter é pontuado (por bloco /64) muito pior pelo anti-bot do YouTube.
        if (config.ForceIPv4)
            yield return "--force-ipv4";

        if (config.CookiesToPass is { } cookies)
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

        // Cliente que já provou funcionar neste servidor (a resolução guarda-o em memória).
        if (config.PlayerClient is { } client)
        {
            yield return "--extractor-args";
            yield return $"youtube:player_client={client}";
        }
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
            .ToArray();

        // Com -v o yt-dlp imprime o traceback todo; a razão real está na linha "ERROR:".
        var error = lines.FirstOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
        if (error != null)
            return error["ERROR:".Length..].Trim();

        var useful = lines
            .Where(l => !IsTracebackLine(l))
            .TakeLast(3)
            .ToArray();

        return useful.Length > 0
            ? string.Join(" | ", useful)
            : "Erro desconhecido no yt-dlp.";
    }

    private static bool IsTracebackLine(string line)
        => line.StartsWith("File \"", StringComparison.Ordinal)
           || line.StartsWith("Traceback", StringComparison.OrdinalIgnoreCase)
           || line.Contains("in raise_no_formats", StringComparison.Ordinal)
           || line.StartsWith("^^^", StringComparison.Ordinal)
           || line.StartsWith("~~~", StringComparison.Ordinal);

    /// <summary>Corta o texto a partir do início ou do fim, conforme o que for mais útil.</summary>
    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[^max..];

    /// <summary>
    /// Teste completo (chamado pelo /diagnostico): corre a mesma cadeia do /play e diz
    /// qual o cliente que funcionou ou, se nada funcionar, o diagnóstico do último erro.
    /// </summary>
    public static async Task<string> DiagnoseAsync(BotConfig config, string url)
    {
        var (info, error) = await ResolveAsync(url, config);

        var header = info != null
            ? "resultado: OK\n"
              + $"cliente: {config.PlayerClient ?? "(o de omissão)"}\n"
              + $"cookies: {(config.UseCookies ? "sim" : "não")}\n"
              + $"título: {info.Title}"
            : "resultado: FALHOU (o IP do servidor está bloqueado)\n"
              + $"cliente: {config.PlayerClient ?? "(nenhum funcionou)"}\n"
              + $"cookies: {(config.UseCookies ? "sim" : "não")}\n\n"
              + error;

        return header;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* ignore */ }
    }
}

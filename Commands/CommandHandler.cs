using System.Text;
using Discord;
using Discord.WebSocket;
using MusicBot.Audio;

namespace MusicBot.Commands;

public static class CommandHandler
{
    public static async Task HandleAsync(SocketSlashCommand command, PlayerManager players, BotConfig config)
    {
        try
        {
            switch (command.Data.Name)
            {
                case "play":
                    await PlayAsync(command, players, config);
                    break;
                case "skip":
                    await SkipAsync(command, players);
                    break;
                case "pause":
                    await PauseAsync(command, players);
                    break;
                case "resume":
                    await ResumeAsync(command, players);
                    break;
                case "queue":
                    await QueueAsync(command, players);
                    break;
                case "nowplaying":
                    await NowPlayingAsync(command, players);
                    break;
                case "stop":
                case "leave":
                    await StopAsync(command, players);
                    break;
                case "diagnostico":
                    await DiagnoseAsync(command, config);
                    break;
                default:
                    await RespondAsync(command, "Comando desconhecido.", ephemeral: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Erro ao executar /{command.Data.Name}", ex);
            await RespondAsync(command, "❌ Ocorreu um erro inesperado.", ephemeral: true);
        }
    }

    private static async Task PlayAsync(SocketSlashCommand command, PlayerManager players, BotConfig config)
    {
        if (command.GuildId is not { } guildId || guildId == 0)
        {
            await RespondAsync(command, "Este comando só funciona dentro de um servidor.", ephemeral: true);
            return;
        }

        var user = command.User as SocketGuildUser;

        // Responder já: o yt-dlp pode demorar segundos e a interação expira aos 3 s.
        try
        {
            await command.DeferAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Interação expirada antes de começar o /play ({ex.Message}).");
            return;
        }

        var (voiceChannel, cacheReady) = await GetVoiceChannelAsync(user, TimeSpan.FromSeconds(6));

        if (!cacheReady)
        {
            await RespondAsync(command,
                "⏳ O Discord ainda está a enviar os dados deste servidor. Tenta outra vez daqui a pouco.",
                ephemeral: true);
            return;
        }

        if (voiceChannel == null)
        {
            await RespondAsync(command, "Entra primeiro num canal de voz para eu poder tocar música. 🎧", ephemeral: true);
            return;
        }

        var query = command.Data.Options.FirstOrDefault(o => o.Name == "query")?.Value as string;
        if (string.IsNullOrWhiteSpace(query))
        {
            await RespondAsync(command, "Diz-me o link do YouTube ou o nome da música.", ephemeral: true);
            return;
        }

        // A pesquisa no YouTube pode demorar alguns segundos.
        var player = players.Get(guildId);
        player.SetChannel(voiceChannel.Id, command.Channel.Id);
        Log.Info($"Pedido: \"{query.Trim()}\" -> #{voiceChannel.Name}");

        var (info, error) = await YtDlp.ResolveAsync(query.Trim(), config, async text =>
        {
            try { await command.FollowupAsync(text, ephemeral: true); }
            catch (Exception ex) { Log.Warn($"Falha a avisar do bloqueio: {ex.Message}"); }
        });

        if (info == null)
        {
            Log.Warn($"Não resolvi \"{query}\": {error}");
            var reason = error ?? "Erro desconhecido no yt-dlp.";
            var details = reason.Length > 1700 ? reason[..1700] : reason;
            await command.FollowupAsync(
                $"❌ Não consegui encontrar essa música.\n```\n{details}\n```",
                ephemeral: true);
            return;
        }

        Log.Ok($"Resolvido: {info.Title} ({info.DurationText})");

        var item = new PlaybackItem(
            info,
            command.User.Id,
            (command.User as SocketGuildUser)?.DisplayName ?? command.User.Username,
            command.Channel.Id);

        var wasPlaying = player.Current != null;

        if (!player.Enqueue(item, config.MaxQueueSize))
        {
            await command.FollowupAsync("🚫 A fila está cheia. Espera que toque algumas músicas.", ephemeral: true);
            return;
        }

        var position = player.QueueCount;

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x5865F2))
            .WithTitle(wasPlaying ? "➕ Adicionada à fila" : "🎶 Vai começar")
            .WithDescription($"**[{info.Title}]({info.Url})**")
            .AddField("Duração", info.DurationText, inline: true)
            .AddField("Posição", wasPlaying ? $"#{position}" : "a seguir", inline: true)
            .WithFooter($"Pedido por {item.RequestedByName}");

        if (!string.IsNullOrWhiteSpace(info.Thumbnail))
            embed.WithThumbnailUrl(info.Thumbnail);

        await command.FollowupAsync(embed: embed.Build());
    }

    private static async Task SkipAsync(SocketSlashCommand command, PlayerManager players)
    {
        var player = GetPlayer(command, players);
        if (player == null || !player.Skip())
        {
            await RespondAsync(command, "Não há nada a tocar para saltar.", ephemeral: true);
            return;
        }

        await RespondAsync(command, "⏭️ Música saltada.");
    }

    private static async Task PauseAsync(SocketSlashCommand command, PlayerManager players)
    {
        var player = GetPlayer(command, players);
        if (player == null || !player.Pause())
        {
            await RespondAsync(command, "Não há nada a tocar.", ephemeral: true);
            return;
        }

        await RespondAsync(command, "⏸️ Música em pausa.");
    }

    private static async Task ResumeAsync(SocketSlashCommand command, PlayerManager players)
    {
        var player = GetPlayer(command, players);
        if (player == null || !player.Resume())
        {
            await RespondAsync(command, "Não há nada em pausa.", ephemeral: true);
            return;
        }

        await RespondAsync(command, "▶️ A continuar a música.");
    }

    private static async Task QueueAsync(SocketSlashCommand command, PlayerManager players)
    {
        var player = GetPlayer(command, players);
        var current = player?.Current;
        var queued = player?.Snapshot() ?? [];

        if (current == null && queued.Count == 0)
        {
            await RespondAsync(command, "📭 A fila está vazia.", ephemeral: true);
            return;
        }

        var description = new StringBuilder();

        if (current != null)
        {
            description.AppendLine($"**A tocar:** [{current.Track.Title}]({current.Track.Url}) `{current.Track.DurationText}`");
            description.AppendLine();
        }

        if (queued.Count > 0)
        {
            description.AppendLine("**A seguir:**");
            for (var i = 0; i < queued.Count && i < 15; i++)
            {
                var item = queued[i];
                description.AppendLine($"`{i + 1}.` [{item.Track.Title}]({item.Track.Url}) `{item.Track.DurationText}`");
            }

            if (queued.Count > 15)
                description.AppendLine($"… e mais {queued.Count - 15}.");
        }

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x5865F2))
            .WithTitle("🎧 Fila de músicas")
            .WithDescription(description.ToString());

        await RespondAsync(command, embed: embed.Build());
    }

    private static async Task NowPlayingAsync(SocketSlashCommand command, PlayerManager players)
    {
        var player = GetPlayer(command, players);
        var current = player?.Current;

        if (current == null)
        {
            await RespondAsync(command, "Não está nada a tocar.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x1DB954))
            .WithAuthor(player!.IsPaused ? "⏸️ Em pausa" : "🎶 A tocar agora")
            .WithTitle(current.Track.Title)
            .WithUrl(current.Track.Url)
            .WithDescription($"Pedido por <@{current.RequestedBy}>")
            .WithFooter($"{current.Track.DurationText} • {current.Track.Uploader ?? "desconhecido"}");

        if (!string.IsNullOrWhiteSpace(current.Track.Thumbnail))
            embed.WithThumbnailUrl(current.Track.Thumbnail);

        await RespondAsync(command, embed: embed.Build());
    }

    private static async Task StopAsync(SocketSlashCommand command, PlayerManager players)
    {
        var player = GetPlayer(command, players);
        if (player == null)
        {
            await RespondAsync(command, "Não estou em nenhum canal de voz.", ephemeral: true);
            return;
        }

        await player.StopAsync(leave: true);
        await RespondAsync(command, "⏹️ Parei a música, limpei a fila e saí do canal.");
    }

    private static GuildPlayer? GetPlayer(SocketSlashCommand command, PlayerManager players)
        => command.GuildId is { } guildId && guildId != 0 ? players.TryGet(guildId) : null;

    /// <summary>
    /// Corre um teste completo ao yt-dlp e mostra o resultado só a quem pediu.
    /// Serve para diagnosticar bloqueios do YouTube sem precisar dos logs do Render.
    /// </summary>
    private static async Task DiagnoseAsync(SocketSlashCommand command, BotConfig config)
    {
        try
        {
            await command.DeferAsync(ephemeral: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Interação expirada antes do /diagnostico ({ex.Message}).");
            return;
        }

        var text = new StringBuilder();
        text.AppendLine("**Diagnóstico**");
        text.AppendLine($"yt-dlp: {config.ResolvedYtDlp ?? "NÃO ENCONTRADO"}");
        text.AppendLine($"cookies: {(config.UseCookies ? config.ResolvedCookies : "desativados")}");
        text.AppendLine($"args extra: {(config.ExtraYtDlpArgs.Length == 0 ? "(nenhum)" : string.Join(' ', config.ExtraYtDlpArgs))}");
        text.AppendLine($"PO provider: {await PotStatusAsync()}");
        text.AppendLine();
        text.AppendLine(await YtDlp.DiagnoseAsync(config, "https://www.youtube.com/watch?v=dQw4w9WgXcQ"));

        var message = text.ToString();
        if (message.Length > 1900)
            message = message[..1900] + "\n…";

        await command.FollowupAsync($"```\n{message}\n```", ephemeral: true);
    }

    private static async Task<string> PotStatusAsync()
    {
        var url = Environment.GetEnvironmentVariable("POT_PROVIDER_URL") ?? "http://127.0.0.1:4416";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{url.TrimEnd('/')}/ping");
            return response.IsSuccessStatusCode
                ? $"OK ({url})"
                : $"respondeu {(int)response.StatusCode} ({url})";
        }
        catch (Exception ex)
        {
            return $"indisponível ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Lê o canal de voz do utilizador. Se o Discord ainda não enviou os dados do servidor,
    /// a cache interna está vazia e o Discord.Net lança NullReferenceException - nesse caso
    /// espera um pouco e tenta outra vez.
    /// </summary>
    private static async Task<(SocketVoiceChannel? Channel, bool CacheReady)> GetVoiceChannelAsync(
        SocketGuildUser? user, TimeSpan timeout)
    {
        if (user == null) return (null, true);

        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                return (user.VoiceChannel, true);
            }
            catch (NullReferenceException)
            {
                if (DateTime.UtcNow >= deadline)
                    return (null, false);

                await Task.Delay(500);
            }
        }
    }

    private static async Task RespondAsync(
        SocketSlashCommand command, string? text = null, Embed? embed = null, bool ephemeral = false)
    {
        try
        {
            if (command.HasResponded)
                await command.FollowupAsync(text: text, embed: embed, ephemeral: ephemeral);
            else
                await command.RespondAsync(text: text, embed: embed, ephemeral: ephemeral);
        }
        catch (Exception ex)
        {
            Log.Warn($"Falha a responder à interação: {ex.Message}");
        }
    }
}

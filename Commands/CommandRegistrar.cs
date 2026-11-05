using Discord;
using Discord.WebSocket;

namespace MusicBot.Commands;

public static class CommandRegistrar
{
    public static async Task RegisterAsync(DiscordSocketClient client, BotConfig config)
    {
        var commands = new List<SlashCommandBuilder>
        {
            new SlashCommandBuilder()
                .WithName("play")
                .WithDescription("Toca uma música do YouTube (link ou pesquisa)")
                .AddOption("query", ApplicationCommandOptionType.String,
                    "Link do YouTube ou nome da música", isRequired: true),

            new SlashCommandBuilder()
                .WithName("skip")
                .WithDescription("Salta para a próxima música da fila"),

            new SlashCommandBuilder()
                .WithName("pause")
                .WithDescription("Pausa a música atual"),

            new SlashCommandBuilder()
                .WithName("resume")
                .WithDescription("Continua a música que está em pausa"),

            new SlashCommandBuilder()
                .WithName("queue")
                .WithDescription("Mostra a fila de músicas"),

            new SlashCommandBuilder()
                .WithName("nowplaying")
                .WithDescription("Mostra a música que está a tocar"),

            new SlashCommandBuilder()
                .WithName("stop")
                .WithDescription("Para a música, limpa a fila e sai do canal"),

            new SlashCommandBuilder()
                .WithName("leave")
                .WithDescription("Sai do canal de voz"),
        };

        if (config.GuildId != 0)
        {
            var guild = client.GetGuild(config.GuildId)
                        ?? throw new InvalidOperationException(
                            $"O bot não está no servidor com ID {config.GuildId}. Convida-o primeiro ou põe guildId a 0.");

            foreach (var command in commands)
                await guild.CreateApplicationCommandAsync(command.Build());

            Log.Ok($"{commands.Count} comandos registados no servidor {guild.Name} (instantâneo).");
        }
        else
        {
            foreach (var command in commands)
                await client.CreateGlobalApplicationCommandAsync(command.Build());

            Log.Ok($"{commands.Count} comandos globais registados (podem demorar até 1h a aparecer; " +
                   "define guildId no config.json para ser imediato).");
        }
    }
}

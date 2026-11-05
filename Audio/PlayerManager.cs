using Discord.WebSocket;

namespace MusicBot.Audio;

public sealed class PlayerManager(DiscordSocketClient client, BotConfig config)
{
    private readonly Dictionary<ulong, GuildPlayer> _players = [];
    private readonly object _sync = new();

    public GuildPlayer Get(ulong guildId)
    {
        lock (_sync)
        {
            if (_players.TryGetValue(guildId, out var existing))
                return existing;

            var player = new GuildPlayer(client, config, guildId);
            _players[guildId] = player;
            return player;
        }
    }

    public GuildPlayer? TryGet(ulong guildId)
    {
        lock (_sync)
            return _players.GetValueOrDefault(guildId);
    }

    public async Task ShutdownAsync()
    {
        List<GuildPlayer> players;
        lock (_sync)
        {
            players = _players.Values.ToList();
            _players.Clear();
        }

        foreach (var player in players)
            await player.DisposeAsync();
    }
}

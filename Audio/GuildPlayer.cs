using Discord;
using Discord.Audio;
using Discord.WebSocket;

namespace MusicBot.Audio;

public sealed class GuildPlayer : IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ulong _guildId;

    private readonly LinkedList<PlaybackItem> _queue = new();
    private readonly object _sync = new();
    private readonly PauseGate _pause = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _idleWatcher;

    private IAudioClient? _audio;
    private ulong _voiceChannelId;
    private ulong _textChannelId;
    private ulong _lastTextChannelId;
    private ulong _connectedChannelId;
    private CancellationTokenSource? _trackCts;
    private AudioPipeline? _pipeline;
    private PlaybackItem? _current;
    private volatile bool _pumpRunning;
    private DateTime _idleSince = DateTime.UtcNow;

    public GuildPlayer(DiscordSocketClient client, BotConfig config, ulong guildId)
    {
        _client = client;
        _config = config;
        _guildId = guildId;
        _idleWatcher = Task.Run(IdleWatchAsync);
    }

    public bool IsConnected => _audio != null;
    public bool IsPlaying => _pumpRunning;
    public bool IsPaused => _pause.IsPaused;

    public PlaybackItem? Current
    {
        get { lock (_sync) return _current; }
    }

    public int QueueCount
    {
        get { lock (_sync) return _queue.Count; }
    }

    public IReadOnlyList<PlaybackItem> Snapshot()
    {
        lock (_sync) return _queue.ToList();
    }

    public void SetChannel(ulong voiceChannelId, ulong textChannelId)
    {
        _voiceChannelId = voiceChannelId;
        _textChannelId = textChannelId;
        _lastTextChannelId = textChannelId;
    }

    public bool Enqueue(PlaybackItem item, int maxQueueSize)
    {
        lock (_sync)
        {
            if (_pumpRunning && _queue.Count >= maxQueueSize)
                return false;

            _queue.AddLast(item);

            if (!_pumpRunning)
            {
                _pumpRunning = true;
                _ = Task.Run(PumpAsync);
            }

            return true;
        }
    }

    public bool Skip()
    {
        lock (_sync)
        {
            if (_current == null) return false;
        }

        CancelCurrent();
        return true;
    }

    public bool Pause()
    {
        if (_current == null) return false;
        _pause.Pause();
        return true;
    }

    public bool Resume()
    {
        if (!_pause.IsPaused) return false;
        _pause.Resume();
        return true;
    }

    public async Task StopAsync(bool leave)
    {
        lock (_sync)
        {
            _queue.Clear();
        }

        CancelCurrent();

        if (leave)
            await CloseAudioAsync();
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            PlaybackItem? item = null;
            bool finished;

            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    finished = _current != null;
                    _pumpRunning = false;
                    _current = null;
                    _idleSince = DateTime.UtcNow;
                }
                else
                {
                    finished = false;
                    item = _queue.First!.Value;
                    _queue.RemoveFirst();
                    _current = item;
                }
            }

            if (finished)
            {
                await NotifyAsync(_lastTextChannelId, "✅ Fila terminada.");
                return;
            }

            if (item is null)
                return;

            _pause.Resume();

            try
            {
                await PlayItemAsync(item);
            }
            catch (OperationCanceledException)
            {
                // Skip/stop - segue para a próxima.
            }
            catch (VoiceConnectionException ex)
            {
                Log.Error(ex.Message);
                await NotifyAsync(item.TextChannelId, $"❌ {ex.Message}");
                lock (_sync) _queue.Clear();
            }
            catch (Exception ex)
            {
                Log.Error($"Falha ao tocar \"{item.Track.Title}\"", ex);
                await NotifyAsync(item.TextChannelId, $"❌ Não consegui tocar **{item.Track.Title}**.");
            }
            finally
            {
                lock (_sync) _current = null;
                _idleSince = DateTime.UtcNow;
            }
        }
    }

    private async Task PlayItemAsync(PlaybackItem item)
    {
        var audio = await EnsureAudioAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _trackCts = cts;

        using var pipeline = AudioPipeline.Start(item.Track.Url, _config);
        _pipeline = pipeline;

        if (_config.AnnounceNowPlaying)
            await NotifyAsync(item.TextChannelId, embed: BuildNowPlayingEmbed(item));

        using var pcm = audio.CreatePCMStream(AudioApplication.Music);

        try
        {
            await CopyWithPauseAsync(pipeline.Output, pcm, cts.Token);

            if (!cts.IsCancellationRequested)
                await pcm.FlushAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Skip/stop.
        }
        catch when (cts.IsCancellationRequested)
        {
            // O processo foi morto por causa de um skip/stop - erro esperado.
        }
        finally
        {
            _pipeline = null;
            if (ReferenceEquals(_trackCts, cts))
                _trackCts = null;
        }
    }

    private async Task CopyWithPauseAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        // 3840 bytes = 20ms de PCM s16le 48kHz stereo.
        var buffer = new byte[3840];
        long total = 0;

        try
        {
            while (true)
            {
                await _pause.WaitAsync(cancellationToken);

                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read <= 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
            }
        }
        finally
        {
            Log.File($"PCM escrito: {total} bytes (~{total / 192000.0:0.0}s).");
        }
    }

    private async Task<IAudioClient> EnsureAudioAsync()
    {
        if (_audio is { } connected && _connectedChannelId == _voiceChannelId)
            return connected;

        if (_audio != null)
            await CloseAudioAsync();

        var guild = _client.GetGuild(_guildId)
                    ?? throw new VoiceConnectionException("O servidor já não está disponível.");
        var channel = guild.GetVoiceChannel(_voiceChannelId)
                      ?? throw new VoiceConnectionException("Canal de voz indisponível.");

        var permissions = guild.CurrentUser.GetPermissions(channel);
        if (!permissions.Connect)
            throw new VoiceConnectionException($"Não tenho permissão para entrar em #{channel.Name}.");
        if (!permissions.Speak)
            throw new VoiceConnectionException($"Não tenho permissão para falar em #{channel.Name}.");

        try
        {
            Log.Info($"A ligar à voz em #{channel.Name}...");
            var audio = await channel.ConnectAsync(selfDeaf: true);
            audio.Disconnected += OnAudioDisconnected;

            _audio = audio;
            _connectedChannelId = _voiceChannelId;
            Log.Ok($"Ligado à voz em #{channel.Name} (UDP {audio.UdpLatency} ms / WS {audio.Latency} ms).");
            return audio;
        }
        catch (VoiceConnectionException)
        {
            throw;
        }
        catch (NullReferenceException ex)
        {
            // Acontece quando o Discord ainda não enviou os dados completos do servidor.
            throw new VoiceConnectionException(
                "O Discord ainda está a carregar este servidor. Tenta novamente em alguns segundos.", ex);
        }
        catch (Exception ex)
        {
            throw new VoiceConnectionException($"Não consegui entrar no canal de voz ({ex.Message}).", ex);
        }
    }

    private Task OnAudioDisconnected(Exception exception)
    {
        _audio = null;
        _connectedChannelId = 0;
        return Task.CompletedTask;
    }

    private async Task CloseAudioAsync()
    {
        var audio = _audio;
        _audio = null;
        _connectedChannelId = 0;

        if (audio != null)
        {
            try { await audio.StopAsync(); } catch { /* ignore */ }
            try { audio.Dispose(); } catch { /* ignore */ }
        }

        var channel = _client.GetGuild(_guildId)?.GetVoiceChannel(_voiceChannelId);
        if (channel != null)
        {
            try { await channel.DisconnectAsync(); } catch { /* ignore */ }
        }
    }

    private void CancelCurrent()
    {
        try { _trackCts?.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
        _pipeline?.Kill();
        _pause.Resume();
    }

    private async Task IdleWatchAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_config.IdleLeaveMinutes <= 0 || _audio == null || _pumpRunning)
                continue;

            if (DateTime.UtcNow - _idleSince >= TimeSpan.FromMinutes(_config.IdleLeaveMinutes))
                await CloseAudioAsync();
        }
    }

    private Embed BuildNowPlayingEmbed(PlaybackItem item)
    {
        var builder = new EmbedBuilder()
            .WithAuthor("🎶 A tocar agora")
            .WithTitle(item.Track.Title)
            .WithUrl(item.Track.Url)
            .WithDescription($"Pedido por <@{item.RequestedBy}>")
            .WithColor(new Color(0x1DB954))
            .WithFooter($"{item.Track.DurationText} • {item.Track.Uploader ?? "desconhecido"}");

        if (!string.IsNullOrWhiteSpace(item.Track.Thumbnail))
            builder.WithThumbnailUrl(item.Track.Thumbnail);

        return builder.Build();
    }

    private async Task NotifyAsync(ulong channelId, string? text = null, Embed? embed = null)
    {
        var channel = _client.GetGuild(_guildId)?.GetTextChannel(channelId);
        if (channel == null) return;

        try
        {
            await channel.SendMessageAsync(text: text, embed: embed);
        }
        catch (Exception ex)
        {
            Log.Warn($"Não consegui enviar mensagem no canal {channelId}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _lifetime.Cancel(); } catch { /* ignore */ }
        CancelCurrent();

        try { await CloseAudioAsync(); } catch { /* ignore */ }
        try { await _idleWatcher; } catch { /* ignore */ }

        _lifetime.Dispose();
    }
}

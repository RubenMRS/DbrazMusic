using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using MusicBot;
using MusicBot.Audio;
using MusicBot.Commands;

var configPath = FindConfigPath();
var config = BotConfig.Load(configPath);

if (args.Contains("--check"))
{
    await RunChecksAsync(config, configPath);
    return 0;
}

if (args.Contains("--selftest"))
{
    return await SelfTestAsync(config, args);
}

if (args.Contains("--voicetest"))
{
    return await VoiceTestAsync(config);
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "Discord Music Bot";

Log.Info($"Config: {configPath ?? "(variáveis de ambiente)"}");

var token = config.EffectiveToken;
if (string.IsNullOrWhiteSpace(token))
{
    Log.Error("Token em falta. Abre o config.json e cola o token do bot " +
              "(ou define a variável de ambiente DISCORD_TOKEN).");
    return 1;
}

if (token.Length < 58)
{
    Log.Error("O token parece inválido (demasiado curto). Copia outra vez o token em " +
              "Developer Portal -> Bot -> Reset Token. Não uses o Client Secret.");
    return 1;
}

if (config.ResolvedYtDlp == null)
    Log.Warn("yt-dlp não encontrado - /play não vai funcionar. Corre setup.ps1.");
if (config.ResolvedFfmpeg == null)
    Log.Warn("ffmpeg não encontrado - /play não vai funcionar. Corre setup.ps1.");

if (config.ResolvedCookies is { } cookies)
{
    Log.Ok(config.CookiesDisabled
        ? $"Cookies do YouTube: {cookies} (desativados por YTDLP_COOKIES)"
        : $"Cookies do YouTube: {cookies}");
}
else
{
    Log.Warn("Sem cookies do YouTube. Se o /play disser \"Sign in to confirm you're not a bot\", " +
             "exporta os cookies do browser para um ficheiro cookies.txt e indica o caminho " +
             "em cookiesFile no config.json (ou defina a variável COOKIES_FILE).");
}

StartHealthServerIfNeeded();

var socketConfig = new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
    LogLevel = LogSeverity.Debug,
    MessageCacheSize = 0,
    EnableVoiceDaveEncryption = true,
};

var client = new DiscordSocketClient(socketConfig);
var players = new PlayerManager(client, config);
var registered = 0;

client.Log += message =>
{
    var line = $"[{message.Severity}] {message.Source}: {message.Message}";
    if (message.Exception != null)
        line += $" | {message.Exception.Message}";

    Log.File(line);

    if (message.Exception != null)
        Log.Warn($"{message.Source}: {message.Exception.Message}");
    else if (message.Severity <= LogSeverity.Warning)
        Log.Warn($"{message.Source}: {message.Message}");

    return Task.CompletedTask;
};

client.Ready += () =>
{
    Log.Ok($"Ligado como {client.CurrentUser.Username} ({client.CurrentUser.Id})");

    if (Interlocked.Exchange(ref registered, 1) == 0)
    {
        // Nunca bloquear o gateway: registar os comandos em segundo plano.
        _ = Task.Run(async () =>
        {
            try
            {
                await CommandRegistrar.RegisterAsync(client, config);
            }
            catch (Exception ex)
            {
                Log.Error("Falha a registar os comandos slash (o bot continua a funcionar)", ex);
            }
        });
    }

    _ = Task.Run(async () =>
    {
        try { await client.SetGameAsync("/play", type: ActivityType.Listening); }
        catch { /* ignore */ }
    });

    return Task.CompletedTask;
};

client.GuildAvailable += guild =>
{
    Log.Ok($"Servidor disponível: {guild.Name} ({guild.Id})");
    return Task.CompletedTask;
};

client.GuildUnavailable += guild =>
{
    Log.Warn($"Servidor indisponível (o Discord ainda está a enviar os dados): {guild.Name} ({guild.Id})");
    return Task.CompletedTask;
};

client.SlashCommandExecuted += command =>
{
    // O Discord.Net invoca este evento no thread do gateway: nunca fazer aqui trabalho lento.
    _ = Task.Run(() => CommandHandler.HandleAsync(command, players, config));
    return Task.CompletedTask;
};

try
{
    await client.LoginAsync(TokenType.Bot, token);
}
catch (Exception ex)
{
    Log.Error("Não consegui ligar ao Discord. Verifica se o token está correto.", ex);
    return 1;
}

// Confirma o token com uma chamada REST antes de abrir o gateway
// (evita ficar em ciclo infinito de "401 Unauthorized").
try
{
    var application = await client.GetApplicationInfoAsync();
    Log.Ok($"Aplicação: {application.Name}");
}
catch (Exception ex)
{
    Log.Error("Token inválido ou sem permissões. Confirma o token no config.json.", ex);
    return 1;
}

try
{
    await client.StartAsync();
}
catch (Exception ex)
{
    Log.Error("Não consegui abrir a ligação ao gateway do Discord.", ex);
    return 1;
}

var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.TrySetResult();

Log.Info("Bot ligado. Usa Ctrl+C para parar.");
await shutdown.Task;

Log.Info("A desligar...");
await players.ShutdownAsync();

try { await client.StopAsync(); } catch { /* ignore */ }
try { await client.LogoutAsync(); } catch { /* ignore */ }

Log.Ok("Até à próxima!");
return 0;

static void StartHealthServerIfNeeded()
{
    if (!int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port) || port <= 0)
        return;

    var started = DateTime.UtcNow;

    try
    {
        // TcpListener simples: não precisa de ACLs (HttpListener exige admin no Windows).
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
        listener.Start();

        _ = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var c = client;
                        using var stream = c.GetStream();

                        var buffer = new byte[2048];
                        var read = await stream.ReadAsync(buffer);
                        var request = System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Max(read, 0));
                        var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";

                        var uptime = (int)(DateTime.UtcNow - started).TotalSeconds;
                        var body = path.StartsWith("/health")
                            ? $"{{\"status\":\"ok\",\"uptime\":{uptime}}}"
                            : "OK";

                        var response = "HTTP/1.1 200 OK\r\n" +
                                       "Content-Type: application/json; charset=utf-8\r\n" +
                                       $"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n" +
                                       "Connection: close\r\n\r\n" + body;

                        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
                        await stream.FlushAsync();
                    }
                    catch
                    {
                        // Cliente desligou a meio - irrelevante.
                    }
                });
            }
        });

        Log.Ok($"HTTP de saúde na porta {port} (keep-alive do Render).");
    }
    catch (Exception ex)
    {
        Log.Warn($"Não consegui abrir a porta HTTP {port}: {ex.Message}");
    }
}

static string? FindConfigPath()
{
    var candidates = new List<string>
    {
        Path.Combine(Directory.GetCurrentDirectory(), "config.json"),
        Path.Combine(AppContext.BaseDirectory, "config.json"),
    };

    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "config.json"));
    }

    return candidates.FirstOrDefault(File.Exists);
}

static async Task RunChecksAsync(BotConfig config, string? configPath)
{
    Console.WriteLine("=== Verificação do bot ===");
    Console.WriteLine($"config.json : {configPath ?? "(variáveis de ambiente)"}");
    Console.WriteLine($"pasta       : {Directory.GetCurrentDirectory()}");
    Console.WriteLine($".NET        : {Environment.Version} ({Environment.OSVersion})");
    Console.WriteLine($"token       : {(string.IsNullOrWhiteSpace(config.EffectiveToken) ? "EM FALTA" : "definido")}");
    Console.WriteLine($"guildId     : {(config.GuildId == 0 ? "0 (comandos globais)" : config.GuildId.ToString())}");
    Console.WriteLine();

    Console.WriteLine($"yt-dlp      : {config.ResolvedYtDlp ?? "NÃO ENCONTRADO"}");
    Console.WriteLine($"              {await VersionAsync(config.ResolvedYtDlp, "--version")}");
    Console.WriteLine($"ffmpeg      : {config.ResolvedFfmpeg ?? "NÃO ENCONTRADO"}");
    Console.WriteLine($"              {await VersionAsync(config.ResolvedFfmpeg, "-version")}");
    Console.WriteLine($"cookies     : {(config.UseCookies ? config.ResolvedCookies : "desativados")}");
    if (config.ExtraYtDlpArgs.Length > 0)
        Console.WriteLine($"yt-dlp args : {string.Join(' ', config.ExtraYtDlpArgs)}");
    Console.WriteLine();

    string[] natives = ["libsodium.dll", "opus.dll", "libdave.dll"];
    foreach (var native in natives)
    {
        var path = Path.Combine(AppContext.BaseDirectory, native);
        if (!File.Exists(path))
        {
            var inProject = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "natives", native));
            Console.WriteLine($"{native,-14}: {(inProject ? "só na pasta natives/ (faz dotnet build)" : "EM FALTA")}");
            continue;
        }

        try
        {
            var handle = NativeLibrary.Load(path);
            var version = native switch
            {
                "libsodium.dll" => CallNativeString(handle, "sodium_version_string"),
                "opus.dll" => CallNativeString(handle, "opus_get_version_string"),
                _ => null,
            };
            NativeLibrary.Free(handle);

            Console.WriteLine($"{native,-14}: OK{(version != null ? $" ({version})" : "")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{native,-14}: FALHOU - {ex.Message}");
        }
    }

    try
    {
        var daveReady = Discord.LibDave.Dave.CheckAvailability();
        var protocol = daveReady ? Discord.LibDave.Dave.MaxSupportedProtocolVersion.ToString() : "—";
        Console.WriteLine($"libdave E2EE: {(daveReady ? $"OK (protocolo {protocol})" : "INDISPONÍVEL")}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"libdave E2EE: FALHOU - {ex.Message}");
    }

    Console.WriteLine();
    Console.WriteLine("Se estiver tudo OK, corre run.ps1.");
}
static async Task<int> VoiceTestAsync(BotConfig config)
{
    Console.WriteLine("=== Teste de voz no Discord ===");

    var token = config.EffectiveToken;
    if (string.IsNullOrWhiteSpace(token) || token.Length < 58)
    {
        Console.WriteLine("Token inválido/em falta.");
        return 1;
    }

    if (config.GuildId == 0)
    {
        Console.WriteLine("Define guildId no config.json para fazer este teste.");
        return 1;
    }

    var client = new DiscordSocketClient(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
        LogLevel = LogSeverity.Info,
        MessageCacheSize = 0,
        EnableVoiceDaveEncryption = true,
    });

    try
    {
        Discord.LibDave.Dave.SetLogSink((severity, file, line, message) =>
        {
            if (severity != Discord.LibDave.Binding.LoggingSeverity.Verbose)
                Console.WriteLine($"[dave:{severity}] {message}");
        });
    }
    catch { /* ignore */ }

    client.Log += message =>
    {
        Console.WriteLine($"[{message.Severity}] {message.Source}: {message.Message}" +
                          (message.Exception != null ? $" | {message.Exception.Message}" : ""));
        return Task.CompletedTask;
    };

    var finished = new TaskCompletionSource();
    var result = 1;

    client.Ready += () =>
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var guild = client.GetGuild(config.GuildId);
                if (guild == null)
                {
                    Console.WriteLine("Servidor não encontrado.");
                    return;
                }

                // Espera que o Discord envie os dados do servidor.
                for (var i = 0; i < 20 && guild.VoiceChannels.Count == 0; i++)
                    await Task.Delay(500);

                var channel = ChooseVoiceChannel(guild);
                if (channel == null)
                {
                    Console.WriteLine("Não há nenhum canal de voz vazio onde possa entrar:");
                    foreach (var vc in guild.VoiceChannels)
                    {
                        try
                        {
                            var perms = guild.CurrentUser.GetPermissions(vc);
                            var ids = string.Join(",", vc.ConnectedUsers.Select(u => u.Id));
                            Console.WriteLine($"  - #{vc.Name}: {vc.ConnectedUsers.Count} ligado(s) [{ids}] " +
                                              $"connect={perms.Connect} speak={perms.Speak}");
                        }
                        catch (Exception ex) { Console.WriteLine($"  - #{vc.Name}: erro {ex.Message}"); }
                    }
                    return;
                }

                Console.WriteLine($"Canal escolhido: #{channel.Name} " +
                                  $"(connect={guild.CurrentUser.GetPermissions(channel).Connect}, " +
                                  $"speak={guild.CurrentUser.GetPermissions(channel).Speak})");

                Console.WriteLine($"A entrar em #{channel.Name}...");
                var audio = await channel.ConnectAsync(selfDeaf: true);
                Console.WriteLine($"Ligado. UDP {audio.UdpLatency} ms / WS {audio.Latency} ms");

                using var pipeline = AudioPipeline.Start(
                    "https://www.youtube.com/watch?v=dQw4w9WgXcQ", config);
                using var pcm = audio.CreatePCMStream(AudioApplication.Music);

                var buffer = new byte[3840];
                long total = 0;
                var stopwatch = Stopwatch.StartNew();

                while (stopwatch.Elapsed < TimeSpan.FromSeconds(6))
                {
                    var read = await pipeline.Output.ReadAsync(buffer);
                    if (read <= 0) break;
                    await pcm.WriteAsync(buffer.AsMemory(0, read));
                    total += read;
                }

                await pcm.FlushAsync();
                pipeline.Kill();

                Console.WriteLine($"Enviados {total} bytes de PCM (≈ {total / 192000.0:0.0}s).");

                try { await audio.StopAsync(); } catch { }
                audio.Dispose();
                try { await channel.DisconnectAsync(); } catch { }

                result = total > 200_000 ? 0 : 1;
                Console.WriteLine(result == 0 ? "Teste de voz OK!" : "Não foi possível enviar áudio.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Falhou: {ex}");
            }
            finally
            {
                finished.TrySetResult();
            }
        });

        return Task.CompletedTask;
    };

    await client.LoginAsync(TokenType.Bot, token);
    await client.StartAsync();

    await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(90)));

    try { await client.StopAsync(); } catch { }
    try { await client.LogoutAsync(); } catch { }

    return result;
}

static SocketVoiceChannel? ChooseVoiceChannel(SocketGuild guild)
{
    foreach (var channel in guild.VoiceChannels)
    {
        try
        {
            var permissions = guild.CurrentUser.GetPermissions(channel);
            if (channel.ConnectedUsers.Count == 0 && permissions.Connect && permissions.Speak)
                return channel;
        }
        catch
        {
            // cache ainda não carregada
        }
    }

    return null;
}

static async Task<int> SelfTestAsync(BotConfig config, string[] args)
{
    var url = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault()
              ?? "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    Console.WriteLine($"=== Teste completo (sem Discord) ===\nAlvo: {url}\n");

    var (info, error) = await YtDlp.ResolveAsync(url, config);
    if (info == null)
    {
        Console.WriteLine($"FALHOU a resolver: {error}");
        return 1;
    }

    Console.WriteLine($"Título   : {info.Title}");
    Console.WriteLine($"Duração  : {info.DurationText}");
    Console.WriteLine($"Uploader : {info.Uploader}");

    using var pipeline = AudioPipeline.Start(info.Url, config);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var buffer = new byte[3840];
    long total = 0;
    var gotData = false;
    var stopwatch = Stopwatch.StartNew();

    try
    {
        while (true)
        {
            var read = await pipeline.Output.ReadAsync(buffer, timeout.Token);
            if (read <= 0) break;

            if (!gotData)
            {
                // Só conta os 3 segundos depois de o áudio começar a chegar.
                gotData = true;
                stopwatch.Restart();
            }

            total += read;

            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(3))
                break;
        }
    }
    catch (OperationCanceledException)
    {
        // timeout
    }
    finally
    {
        pipeline.Kill();
    }

    Console.WriteLine($"PCM recebido: {total} bytes (esperado ~{3 * 48000 * 2 * 2})");

    if (total < 100_000)
    {
        Console.WriteLine("FALHOU: não veio áudio suficiente.");
        return 1;
    }

    Console.WriteLine("Tudo OK!");
    return 0;
}

static async Task<string> VersionAsync(string? exe, string arg)
{
    if (exe == null) return "—";

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }.WithArgument(arg));

        if (process == null) return "não arrancou";

        var output = await process.StandardOutput.ReadLineAsync();
        await process.WaitForExitAsync();
        return string.IsNullOrWhiteSpace(output) ? "sem versão" : output.Trim();
    }
    catch (Exception ex)
    {
        return $"erro: {ex.Message}";
    }
}

static string? CallNativeString(IntPtr module, string export)
{
    if (!NativeLibrary.TryGetExport(module, export, out var address))
        return null;

    try
    {
        var function = Marshal.GetDelegateForFunctionPointer<NativeStringDelegate>(address);
        return Marshal.PtrToStringAnsi(function());
    }
    catch
    {
        return null;
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArgument(this ProcessStartInfo info, string argument)
    {
        info.ArgumentList.Add(argument);
        return info;
    }
}

internal delegate IntPtr NativeStringDelegate();

using System.Diagnostics;

namespace MusicBot.Audio;

/// <summary>
/// Pipeline yt-dlp -> ffmpeg -> PCM 48kHz stereo.
/// O yt-dlp descarrega o áudio para stdout e o ffmpeg converte-o para o formato do Discord.
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private readonly Process _ytDlp;
    private readonly Process _ffmpeg;
    private readonly Task _feedTask;

    public Stream Output => _ffmpeg.StandardOutput.BaseStream;

    private AudioPipeline(Process ytDlp, Process ffmpeg)
    {
        _ytDlp = ytDlp;
        _ffmpeg = ffmpeg;

        _feedTask = Task.Run(async () =>
        {
            try
            {
                await _ytDlp.StandardOutput.BaseStream
                    .CopyToAsync(_ffmpeg.StandardInput.BaseStream)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Processo morto/parado - normal em skip/stop.
            }
            finally
            {
                try { _ffmpeg.StandardInput.Close(); } catch { /* ignore */ }
            }
        });

        // Drain dos erros para os processos não bloquearem.
        _ = Task.Run(async () => await DrainAsync(_ffmpeg.StandardError));
        _ = Task.Run(async () =>
        {
            var stderr = await DrainAsync(_ytDlp.StandardError);
            if (!string.IsNullOrWhiteSpace(stderr))
                Log.Warn($"yt-dlp: {stderr}");
        });
    }

    public static AudioPipeline Start(string url, BotConfig config)
    {
        if (config.ResolvedYtDlp is not { } ytDlp)
            throw new InvalidOperationException("yt-dlp não encontrado.");
        if (config.ResolvedFfmpeg is not { } ffmpeg)
            throw new InvalidOperationException("ffmpeg não encontrado.");

        var ffmpegArgs = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-i", "pipe:0",
            "-vn",
            "-ac", "2",
            "-ar", "48000",
            "-f", "s16le",
            "pipe:1",
        };
        ffmpegArgs.AddRange(config.ExtraFfmpegArgs);

        var ytArgs = new List<string>();
        ytArgs.AddRange(YtDlp.BuildBaseArgs(config));
        ytArgs.AddRange(["--quiet", "--no-progress", "-f", "bestaudio/best", "-o", "-"]);
        ytArgs.Add(url);

        var ffmpegProcess = StartProcess(ffmpeg, ffmpegArgs, redirectInput: true);
        Process? ytProcess = null;
        try
        {
            ytProcess = StartProcess(ytDlp, ytArgs, redirectInput: false);
            return new AudioPipeline(ytProcess, ffmpegProcess);
        }
        catch
        {
            TryKill(ffmpegProcess);
            if (ytProcess != null) TryKill(ytProcess);
            throw;
        }
    }

    private static Process StartProcess(string fileName, IEnumerable<string> args, bool redirectInput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"Não consegui iniciar {Path.GetFileName(fileName)}.");
        return process;
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        try
        {
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            var trimmed = text.Trim();
            return trimmed.Length > 500 ? trimmed[^500..] : trimmed;
        }
        catch
        {
            return "";
        }
    }

    public void Kill()
    {
        TryKill(_ffmpeg);
        TryKill(_ytDlp);
    }

    public void Dispose()
    {
        Kill();
        try { _ytDlp.Dispose(); } catch { /* ignore */ }
        try { _ffmpeg.Dispose(); } catch { /* ignore */ }
        _ = _feedTask;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }
}

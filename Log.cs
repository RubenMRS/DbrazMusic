namespace MusicBot;

public static class Log
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(Directory.GetCurrentDirectory(), "bot.log");
    private const long MaxBytes = 5 * 1024 * 1024;

    private static bool _checked;

    public static void Info(string message) => Write(ConsoleColor.Gray, "INFO", message);
    public static void Ok(string message) => Write(ConsoleColor.Green, " OK ", message);
    public static void Warn(string message) => Write(ConsoleColor.Yellow, "WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        Write(ConsoleColor.Red, "ERRO", message);
        if (ex != null)
        {
            Console.Error.WriteLine(ex);
            Append($"[{DateTime.Now:HH:mm:ss}] [ERRO] {ex}");
        }
    }

    /// <summary>Só escreve no bot.log (para diagnóstico sem encher a consola).</summary>
    public static void File(string message) => Append($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static void Write(ConsoleColor color, string level, string message)
    {
        lock (Sync)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] [{level}] ");
            Console.ForegroundColor = previous;
            Console.WriteLine(message);

            Append($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }
    }

    private static void Append(string line)
    {
        try
        {
            lock (Sync)
            {
                if (!_checked)
                {
                    _checked = true;
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                        System.IO.File.Delete(FilePath);
                }

                System.IO.File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // O diagnóstico nunca deve derrubar o bot.
        }
    }
}

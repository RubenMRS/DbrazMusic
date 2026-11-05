namespace MusicBot;

/// <summary>
/// Encontra ferramentas (yt-dlp/ffmpeg) quer o bot seja corrido com `dotnet run`,
/// com o .exe compilado, ou com os binários no PATH.
/// </summary>
public static class ToolLocator
{
    public static string? Resolve(string? configured, string fileName)
    {
        // No Linux o binário não tem ".exe"; no Windows tem.
        var configuredVariants = new List<string?>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            configuredVariants.Add(configured);
            if (configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                configuredVariants.Add(configured[..^4]);
        }
        configuredVariants.Add(null);

        foreach (var name in NameVariants(fileName))
        {
            foreach (var configuredVariant in configuredVariants)
            {
                foreach (var candidate in Candidates(configuredVariant, name))
                {
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> NameVariants(string fileName)
    {
        yield return fileName;

        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            yield return fileName[..^4];
        else
            yield return fileName + ".exe";
    }

    public static string? ResolveOptional(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (File.Exists(configured)) return Path.GetFullPath(configured);
        return Resolve(configured, Path.GetFileName(configured));
    }

    private static IEnumerable<string> Candidates(string? configured, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
            if (Path.IsPathRooted(configured) is false)
            {
                yield return Path.Combine(Directory.GetCurrentDirectory(), configured);
                yield return Path.Combine(AppContext.BaseDirectory, configured);
            }
        }

        var dirs = new List<string> { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current?.Parent != null; i++)
        {
            current = current.Parent;
            dirs.Add(current.FullName);
        }

        foreach (var dir in dirs)
        {
            yield return Path.Combine(dir, fileName);
            yield return Path.Combine(dir, "tools", fileName);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), fileName); }
            catch { continue; }
            yield return candidate;
        }
    }
}

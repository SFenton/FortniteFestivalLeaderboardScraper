namespace PercentileService;

/// <summary>
/// Loads environment variables from a .env file.
/// </summary>
public static class EnvFileLoader
{
    /// <summary>
    /// Parse lines from a .env file and set them as environment variables.
    /// Lines starting with '#' are comments. Blank lines are ignored.
    /// Values may be optionally quoted with double quotes.
    /// </summary>
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

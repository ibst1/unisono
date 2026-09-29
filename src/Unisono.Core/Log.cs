namespace Unisono;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Console (CLI), an event (tray app) and a daily file under the data dir\log, so any instance can be inspected afterwards.</summary>
public sealed class Log
{
    private readonly string _dir = Path.Combine(AppPaths.DataDir, "log");
    private readonly object _gate = new();
    public bool Verbose { get; set; }
    public bool ToConsole { get; set; } = true;

    /// <summary>Raised for every line that passes the verbosity filter; may fire on any thread.</summary>
    public event Action<LogLevel, string>? Written;

    public string Directory => _dir;

    /// <summary>An exception's message, or its type and HRESULT when it has none (some Windows APIs throw without text).</summary>
    public static string Text(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? $"{ex.GetType().Name} (0x{ex.HResult:X8})" : ex.Message;

    public void Info(string message) => Write(LogLevel.Info, message, ConsoleColor.Gray);
    public void Warn(string message) => Write(LogLevel.Warn, message, ConsoleColor.Yellow);
    public void Error(string message) => Write(LogLevel.Error, message, ConsoleColor.Red);
    public void Debug(string message) { if (Verbose) Write(LogLevel.Debug, message, ConsoleColor.DarkGray); }

    private void Write(LogLevel level, string message, ConsoleColor color)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss} {level.ToString().ToLowerInvariant(),-5} {message}";
        lock (_gate)
        {
            if (ToConsole)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = prev;
            }
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"unisono-{now:yyyy-MM-dd}.log"), $"{now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch { /* logging must never take the sync down */ }
        }
        try { Written?.Invoke(level, line); } catch { /* a UI that is closing must not break the sync */ }
    }
}

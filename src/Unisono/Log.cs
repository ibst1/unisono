namespace Unisono;

/// <summary>Console plus a daily file under %LOCALAPPDATA%\Unisono\log, so a tray-only instance can still be inspected.</summary>
public sealed class Log
{
    private readonly string _dir = Path.Combine(AppPaths.DataDir, "log");
    private readonly object _gate = new();
    public bool Verbose { get; set; }

    public void Info(string message) => Write("info", message, ConsoleColor.Gray);
    public void Warn(string message) => Write("warn", message, ConsoleColor.Yellow);
    public void Error(string message) => Write("error", message, ConsoleColor.Red);
    public void Debug(string message) { if (Verbose) Write("debug", message, ConsoleColor.DarkGray); }

    private void Write(string level, string message, ConsoleColor color)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss} {level,-5} {message}";
        lock (_gate)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"unisono-{now:yyyy-MM-dd}.log"), $"{now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch { /* logging must never take the sync down */ }
        }
    }
}

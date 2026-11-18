namespace ParkedTraffic;

/// <summary>Central log. Writes to the console until a UI subscribes (the TUI must not have raw console writes).</summary>
public static class Log
{
    public static event Action<string>? Line;

    public static void Write(string message)
    {
        var handler = Line;
        if (handler is null) Console.WriteLine(message);
        else handler($"{DateTime.Now:HH:mm:ss} {message}");
    }
}

namespace DeadlockVmdlCompiler.Services;

/// <summary>
/// Lets static services report recoverable problems; the main window shows them in its log.
/// Messages may arrive from any thread.
/// </summary>
public static class AppLog
{
    public static event Action<string>? Message;

    public static void Warn(string message) => Message?.Invoke("[warn] " + message);
}

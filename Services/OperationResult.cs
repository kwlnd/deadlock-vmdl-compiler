namespace DeadlockVmdlCompiler.Services;

/// <summary>Outcome of a user-facing operation: whether it worked, what to tell the user, and whether files changed.</summary>
public sealed record OperationResult(bool Success, string Message, bool Changed = false)
{
    public static OperationResult Ok(string message, bool changed = false) => new(true, message, changed);
    public static OperationResult Fail(string message) => new(false, message);

    public void Deconstruct(out bool success, out string message)
    {
        success = Success;
        message = Message;
    }
}

/// <summary>Result of editing ModelDoc text. <see cref="Error"/> is set when the edit could not be made.</summary>
public sealed record ModelDocEdit(string Content, IReadOnlyList<string> Changes, string? Error = null)
{
    public bool Failed => Error != null;
}

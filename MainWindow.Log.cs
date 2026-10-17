using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler;

// The colour-coded log console.
public partial class MainWindow
{
    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Dispatcher.UIThread.Post(() =>
        {
            _logBuffer.AppendLine(line);
            _pendingLogLines.Add(line);

            // Truncate buffer if exceedingly large (keep latest ~150k chars) to preserve UI responsiveness
            if (_logBuffer.Length > 200_000)
            {
                var content = _logBuffer.ToString();
                var splitIdx = content.IndexOf('\n', 50_000);
                if (splitIdx > 0)
                {
                    _logBuffer.Clear();
                    _logBuffer.Append(content.Substring(splitIdx + 1));
                }
            }

            // Chatty compiler output arrives line by line; repaint once per burst.
            if (_logFlushPending) return;
            _logFlushPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _logFlushPending = false;
                FlushLogLines();
                if (ChkAutoScroll?.IsChecked == true)
                    ScrollLog?.ScrollToEnd();
            }, DispatcherPriority.Background);
        });
    }

    private void FlushLogLines()
    {
        var inlines = TxtLog.Inlines ??= new InlineCollection();
        foreach (var line in _pendingLogLines)
        {
            // "[HH:mm:ss] " is dimmed; the message takes the colour of its outcome.
            var split = line.StartsWith('[') && line.Length > 11 && line[9] == ']' ? 11 : 0;
            if (split > 0) inlines.Add(new Run(line[..split]) { Foreground = LogTime });
            inlines.Add(new Run(line[split..]) { Foreground = LogBrush(line[split..]) });
            inlines.Add(new LineBreak());
            _logLineInlines.Enqueue(split > 0 ? 3 : 2);
        }
        _pendingLogLines.Clear();
        // Keep the console responsive: drop the oldest lines past the limit.
        while (_logLineInlines.Count > MaxLogLines)
        {
            var count = _logLineInlines.Dequeue();
            inlines.RemoveRange(0, Math.Min(count, inlines.Count));
        }
    }

    private static IBrush LogBrush(string message)
    {
        static bool Has(string text, params string[] words) =>
            words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has(message, "error", "failed", "fatal", "exception", "refus", "invalid", "not found", "missing:"))
            return LogError;
        if (Has(message, "warn", "skipped", "missing", "cancel", "outdated", "not detected"))
            return LogWarning;
        if (Has(message, "success", "passed", "ready", "synced", "applied", "verified", "created", "copied", "complete", "done"))
            return LogSuccess;
        return LogText;
    }

    private void BtnClearLog_Click(object? sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        _pendingLogLines.Clear();
        TxtLog.Inlines?.Clear();
        _logLineInlines.Clear();
    }

    private async void BtnCopyLog_Click(object? sender, RoutedEventArgs e)
    {
        var text = _logBuffer.ToString();
        if (!string.IsNullOrEmpty(text))
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
                Log("[log] console text copied to clipboard");
            }
        }
    }
}

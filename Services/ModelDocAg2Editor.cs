using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DeadlockVmdlCompiler.Services;

/// <summary>Edits ModelDoc node arrays without relying on optional RootNode fields or line layout.</summary>
internal static class ModelDocAg2Editor
{
    private readonly record struct Node(int Start, int End, string ClassName);

    internal static IEnumerable<string> GetAnimationSourcePaths(string content) =>
        EnumerateAllNodes(content)
            .Where(node => node.ClassName.Equals("AnimFile", StringComparison.OrdinalIgnoreCase))
            .Select(node => FieldValue(content, node, "source_filename") is { Length: > 0 } source
                ? source : FieldValue(content, node, "filename"))
            .Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    internal static (string Content, int FoundCount, int MissingCount) ApplyAnimationFileAvailability(
        string content, Func<string, bool> sourceExists)
    {
        var nodes = EnumerateAllNodes(content)
            .Where(node => node.ClassName.Equals("AnimFile", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(node => node.Start).ToArray();
        var found = 0;
        var missing = 0;
        var activeFound = 0;
        var activeMissing = 0;
        foreach (var node in nodes)
        {
            var filename = FieldValue(content, node, "source_filename");
            if (filename.Length == 0) filename = FieldValue(content, node, "filename");
            var muted = IsNodeDisabled(content, node);
            if (filename.Length > 0 && sourceExists(filename))
            {
                found++;
                if (!muted) activeFound++;
                continue; // Keep a deliberate per-clip disabled flag.
            }
            missing++;
            if (muted) continue;
            activeMissing++;
            var block = content.Substring(node.Start, node.End - node.Start);
            block = SetNodeDisabled(block, "AnimFile", disabled: true);
            content = content.Remove(node.Start, node.End - node.Start).Insert(node.Start, block);
        }
        content = SetNodeDisabled(content, "AnimationList", activeFound == 0 && activeMissing > 0);
        return (content, found, missing);
    }

    private static bool IsNodeDisabled(string content, Node node) =>
        DirectFieldMatches(content, node, "disabled", @"true\b").Any();

    /// <summary>Removes every node of the class, wherever it is nested.</summary>
    internal static string RemoveNodes(string content, string className)
    {
        var outermost = new List<Node>();
        foreach (var node in EnumerateAllNodes(content)
                     .Where(node => node.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(node => node.Start))
        {
            if (outermost.Count == 0 || node.Start >= outermost[^1].End) outermost.Add(node);
        }

        foreach (var node in outermost.AsEnumerable().Reverse())
        {
            var end = node.End;
            while (end < content.Length && content[end] is ' ' or '\t') end++;
            if (end < content.Length && content[end] == ',') end++;
            while (end < content.Length && content[end] is '\r' or '\n') end++;
            var start = node.Start;
            while (start > 0 && content[start - 1] is ' ' or '\t') start--;
            content = content.Remove(start, end - start);
        }
        return content;
    }

    private static IEnumerable<Node> EnumerateAllNodes(string content)
    {
        var starts = new Stack<int>();
        for (var i = 0; i < content.Length; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i) { i = skipped - 1; continue; }
            if (content[i] == '{') starts.Push(i);
            else if (content[i] == '}' && starts.Count > 0)
            {
                var node = new Node(starts.Pop(), i + 1, string.Empty);
                yield return node with { ClassName = FieldValue(content, node, "_class") };
            }
        }
    }

    internal static string SetNodeDisabled(string content, string className, bool disabled)
    {
        var classPattern = new Regex(@"\b_class\s*=\s*""" + Regex.Escape(className) + @"""",
            RegexOptions.IgnoreCase);
        var value = disabled ? "true" : "false";
        var searchStart = 0;

        while (searchStart < content.Length)
        {
            var classMatch = classPattern.Match(content, searchStart);
            if (!classMatch.Success) break;
            searchStart = classMatch.Index + classMatch.Length;
            if (IsIgnoredAt(content, classMatch.Index)) continue;

            var nodeStart = FindEnclosingOpenBrace(content, classMatch.Index);
            if (nodeStart < 0) continue;
            var nodeClose = FindMatching(content, nodeStart, '{', '}');
            if (nodeClose < 0) continue;
            var node = new Node(nodeStart, nodeClose + 1, className);
            if (!IsDirectField(content, node, classMatch.Index)) continue;

            var block = content.Substring(node.Start, node.End - node.Start);
            var fields = DirectFieldMatches(content, node, "disabled", @"(true|false)\b").ToList();
            if (fields.Count > 0)
            {
                foreach (var field in fields.AsEnumerable().Reverse())
                {
                    var oldValue = field.Groups[1];
                    block = block.Remove(oldValue.Index - node.Start, oldValue.Length)
                        .Insert(oldValue.Index - node.Start, value);
                }
            }
            else
            {
                var lineStart = content.LastIndexOf('\n', classMatch.Index) + 1;
                var indent = content.Substring(lineStart, classMatch.Index - lineStart);
                if (indent.Any(c => c is not (' ' or '\t'))) indent = "\t";
                var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                block = block.Insert(classMatch.Index + classMatch.Length - node.Start,
                    newline + indent + "disabled = " + value);
            }

            content = content.Remove(node.Start, node.End - node.Start).Insert(node.Start, block);
            searchStart = node.Start + block.Length;
        }

        return content;
    }

    private static int FindEnclosingOpenBrace(string content, int position)
    {
        var openBraces = new Stack<int>();
        for (var i = 0; i < position; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i) { i = skipped - 1; continue; }
            if (content[i] == '{') openBraces.Push(i);
            else if (content[i] == '}' && openBraces.Count > 0) openBraces.Pop();
        }
        return openBraces.Count > 0 ? openBraces.Peek() : -1;
    }

    public static (string Content, List<string> Changes) Upgrade(
        string content, string skelPath, string graphPath, string? uiGraphPath,
        bool addSkel, bool addGraph, bool addUiGraph, bool upgradeHeader, string header,
        IReadOnlyDictionary<string, string>? namedGraphs = null)
    {
        var changes = new List<string>();
        if (upgradeHeader)
        {
            var lineEnd = content.IndexOfAny(['\r', '\n']);
            var firstLine = lineEnd < 0 ? content : content[..lineEnd];
            if (firstLine.Contains("format:modeldoc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(firstLine.Trim(), header, StringComparison.Ordinal))
            {
                content = header + (lineEnd < 0 ? string.Empty : content[lineEnd..]);
                changes.Add("Upgraded header format to modeldoc41");
            }
        }

        var extraGraphs = addGraph ? namedGraphs : null;
        var addDefaultGraph = addGraph && !string.IsNullOrWhiteSpace(graphPath);
        if ((addSkel && !ValidPath(skelPath)) ||
            (addGraph && !addDefaultGraph && extraGraphs is not { Count: > 0 }) ||
            (addDefaultGraph && !ValidPath(graphPath)) ||
            (extraGraphs?.Any(pair => !ValidPath(pair.Key) || !ValidPath(pair.Value) ||
                pair.Key.Equals("ui", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("default", StringComparison.OrdinalIgnoreCase)) == true) ||
            (addUiGraph && !ValidPath(uiGraphPath)))
        {
            changes.Add("Error: Selected AG2 reference path is empty or invalid; select a hero preset.");
            return (content, changes);
        }

        if (!addSkel && !addGraph && !addUiGraph)
            return (content, changes);

        if (!TryGetRootChildren(content, out var rootOpen, out var rootClose))
        {
            changes.Add("Error: Could not locate rootNode children array.");
            return (content, changes);
        }

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (addSkel)
        {
            content = WrapStandaloneNodes(content, rootOpen, rootClose, "NmSkeletonList",
                ["NmSkeletonReference"], newline, out var wrappedSkeleton);
            if (wrappedSkeleton)
            {
                changes.Add("Moved standalone NmSkeletonReference into NmSkeletonList");
                TryGetRootChildren(content, out rootOpen, out rootClose);
            }
            var list = FindNode(content, rootOpen, rootClose, "NmSkeletonList");
            if (list is null)
            {
                content = InsertIntoArray(content, rootOpen, rootClose, ListNode("NmSkeletonList", [ReferenceNode(newline, "NmSkeletonReference", skelPath)], newline), newline);
                changes.Add("Injected NmSkeletonList node");
            }
            else if (!TryGetChildren(content, list.Value, out var childOpen, out var childClose))
            {
                changes.Add("Error: NmSkeletonList has no children array.");
                return (content, changes);
            }
            else
            {
                var reference = FindNode(content, childOpen, childClose, "NmSkeletonReference");
                if (reference is null)
                {
                    content = InsertIntoArray(content, childOpen, childClose, ReferenceNode(newline, "NmSkeletonReference", skelPath), newline);
                    changes.Add("Injected NmSkeletonReference node");
                }
                else
                {
                    content = SetFilename(content, reference.Value, skelPath, newline, out var changed);
                    if (changed) changes.Add("Updated NmSkeletonReference path");
                }
            }
        }

        if (addDefaultGraph || addUiGraph || extraGraphs is { Count: > 0 })
        {
            if (!TryGetRootChildren(content, out rootOpen, out rootClose))
            {
                changes.Add("Error: Could not locate rootNode children array after editing skeleton.");
                return (content, changes);
            }

            content = WrapStandaloneNodes(content, rootOpen, rootClose, "AnimGraph2List",
                ["DefaultAnimGraph2", "AnimGraph2"], newline, out var wrappedGraphs);
            if (wrappedGraphs)
            {
                changes.Add("Moved standalone graph nodes into AnimGraph2List");
                TryGetRootChildren(content, out rootOpen, out rootClose);
            }
            var list = FindNode(content, rootOpen, rootClose, "AnimGraph2List");
            if (list is null)
            {
                var children = new List<string>();
                if (addDefaultGraph) children.Add(ReferenceNode(newline, "DefaultAnimGraph2", graphPath));
                if (addUiGraph) children.Add(ReferenceNode(newline, "AnimGraph2", uiGraphPath!, "ui"));
                if (extraGraphs != null)
                    children.AddRange(extraGraphs.Select(pair => ReferenceNode(newline, "AnimGraph2", pair.Value, pair.Key)));
                content = InsertIntoArray(content, rootOpen, rootClose, ListNode("AnimGraph2List", children, newline), newline);
                changes.Add("Injected AnimGraph2List node");
            }
            else
            {
                if (!TryGetChildren(content, list.Value, out var childOpen, out var childClose))
                {
                    changes.Add("Error: AnimGraph2List has no children array.");
                    return (content, changes);
                }

                if (addDefaultGraph)
                {
                    var reference = FindNode(content, childOpen, childClose, "DefaultAnimGraph2");
                    if (reference is null)
                    {
                        content = InsertIntoArray(content, childOpen, childClose, ReferenceNode(newline, "DefaultAnimGraph2", graphPath), newline);
                        changes.Add("Injected DefaultAnimGraph2 node");
                    }
                    else
                    {
                        content = SetFilename(content, reference.Value, graphPath, newline, out var changed);
                        if (changed) changes.Add("Updated DefaultAnimGraph2 path");
                    }
                }

                if (addUiGraph)
                {
                    if (!TryGetRootChildren(content, out rootOpen, out rootClose) ||
                        (list = FindNode(content, rootOpen, rootClose, "AnimGraph2List")) is null ||
                        !TryGetChildren(content, list.Value, out childOpen, out childClose))
                    {
                        changes.Add("Error: Could not reopen AnimGraph2List after editing default graph.");
                        return (content, changes);
                    }
                    var reference = EnumerateNodes(content, childOpen, childClose).FirstOrDefault(n =>
                        n.ClassName.Equals("AnimGraph2", StringComparison.OrdinalIgnoreCase) &&
                        FieldValue(content, n, "name").Equals("ui", StringComparison.OrdinalIgnoreCase));
                    if (reference == default)
                    {
                        content = InsertIntoArray(content, childOpen, childClose, ReferenceNode(newline, "AnimGraph2", uiGraphPath!, "ui"), newline);
                        changes.Add("Injected ui AnimGraph2 node");
                    }
                    else
                    {
                        content = SetFilename(content, reference, uiGraphPath!, newline, out var changed);
                        if (changed) changes.Add("Updated ui AnimGraph2 path");
                    }
                }
                if (extraGraphs != null)
                {
                    foreach (var (name, path) in extraGraphs)
                    {
                        if (!TryGetRootChildren(content, out rootOpen, out rootClose) ||
                            (list = FindNode(content, rootOpen, rootClose, "AnimGraph2List")) is null ||
                            !TryGetChildren(content, list.Value, out childOpen, out childClose))
                        {
                            changes.Add("Error: Could not reopen AnimGraph2List after editing named graphs.");
                            return (content, changes);
                        }
                        var reference = EnumerateNodes(content, childOpen, childClose).FirstOrDefault(node =>
                            node.ClassName.Equals("AnimGraph2", StringComparison.OrdinalIgnoreCase) &&
                            FieldValue(content, node, "name").Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (reference == default)
                        {
                            content = InsertIntoArray(content, childOpen, childClose, ReferenceNode(newline, "AnimGraph2", path, name), newline);
                            changes.Add($"Injected {name} AnimGraph2 node");
                        }
                        else
                        {
                            content = SetFilename(content, reference, path, newline, out var changed);
                            if (changed) changes.Add($"Updated {name} AnimGraph2 path");
                        }
                    }
                }
            }
        }

        return (content, changes);
    }

    private static bool ValidPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !path.Any(c => c is '"' or '\r' or '\n' or '\0');

    private static string ReferenceNode(string newline, string className, string path, string? name = null) =>
        "{" + newline + "_class = \"" + className + "\"" + newline +
        (name is null ? string.Empty : "name = \"" + name + "\"" + newline) +
        "filename = \"" + path.Replace('\\', '/') + "\"" + newline + "}";

    private static string ListNode(string className, IEnumerable<string> children, string newline) =>
        "{" + newline + "_class = \"" + className + "\"" + newline +
        "children =" + newline + "[" + newline +
        string.Join("," + newline, children) + newline + "]" + newline + "}";

    private static string WrapStandaloneNodes(string content, int open, int close, string listClass,
        string[] childClasses, string newline, out bool wrapped)
    {
        wrapped = false;
        if (FindNode(content, open, close, listClass) is not null) return content;
        var standalone = EnumerateNodes(content, open, close)
            .Where(node => childClasses.Any(name => name.Equals(node.ClassName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (standalone.Count == 0) return content;

        var children = standalone.Select(node => content.Substring(node.Start, node.End - node.Start)).ToList();
        foreach (var node in standalone.AsEnumerable().Reverse())
            content = RemoveFromArray(content, open, node);

        if (!TryGetRootChildren(content, out open, out close)) return content;
        wrapped = true;
        return InsertIntoArray(content, open, close, ListNode(listClass, children, newline), newline);
    }

    private static string RemoveFromArray(string content, int open, Node node)
    {
        var start = node.Start;
        var end = node.End;
        while (end < content.Length && char.IsWhiteSpace(content[end])) end++;
        if (end < content.Length && content[end] == ',') end++;
        else
        {
            while (start > open + 1 && char.IsWhiteSpace(content[start - 1])) start--;
            if (start > open + 1 && content[start - 1] == ',') start--;
        }
        return content.Remove(start, end - start);
    }

    private static bool TryGetRootChildren(string content, out int open, out int close)
    {
        open = close = -1;
        var root = Regex.Matches(content, @"\brootNode\s*=\s*\{", RegexOptions.IgnoreCase)
            .Cast<Match>().FirstOrDefault(match => !IsIgnoredAt(content, match.Index));
        if (root is null) return false;
        var rootOpen = content.IndexOf('{', root.Index);
        var rootClose = FindMatching(content, rootOpen, '{', '}');
        if (rootClose < 0) return false;
        return TryGetChildren(content, new Node(rootOpen, rootClose + 1, "RootNode"), out open, out close);
    }

    private static bool TryGetChildren(string content, Node node, out int open, out int close)
    {
        open = close = -1;
        var match = DirectFieldMatches(content, node, "children", @"\[").FirstOrDefault();
        if (match is null) return false;
        open = match.Index + match.Length - 1;
        close = FindMatching(content, open, '[', ']');
        return close >= 0 && close < node.End;
    }

    private static Node? FindNode(string content, int open, int close, string className)
    {
        foreach (var node in EnumerateNodes(content, open, close))
            if (node.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase)) return node;
        return null;
    }

    private static IEnumerable<Node> EnumerateNodes(string content, int open, int close)
    {
        for (var i = open + 1; i < close; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i) { i = skipped - 1; continue; }
            if (content[i] != '{') continue;
            var end = FindMatching(content, i, '{', '}');
            if (end < 0 || end >= close) yield break;
            var node = new Node(i, end + 1, string.Empty);
            yield return node with { ClassName = FieldValue(content, node, "_class") };
            i = end;
        }
    }

    private const string StringValue = @"""([^""]*)""";
    private static readonly ConcurrentDictionary<string, Regex> FieldPatterns = new();

    private static string FieldValue(string content, Node node, string field) =>
        DirectFieldMatches(content, node, field, StringValue).FirstOrDefault()?.Groups[1].Value ?? string.Empty;

    /// <summary>
    /// Finds "field = value" among the node's own fields in one pass, without
    /// rescanning the document for each candidate inside nested nodes.
    /// </summary>
    private static IEnumerable<Match> DirectFieldMatches(string content, Node node, string field, string valuePattern)
    {
        var pattern = FieldPatterns.GetOrAdd(field + "\0" + valuePattern, _ => new Regex(
            @"\G" + Regex.Escape(field) + @"\s*=\s*" + valuePattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        var depth = 0;
        for (var i = node.Start + 1; i < node.End - 1; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i) { i = skipped - 1; continue; }
            var c = content[i];
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (IsWordChar(c) && !IsWordChar(content[i - 1]))
            {
                if (depth == 0 && string.Compare(content, i, field, 0, field.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    var match = pattern.Match(content, i);
                    if (match.Success && match.Index + match.Length <= node.End) yield return match;
                }
                while (i + 1 < node.End && IsWordChar(content[i + 1])) i++;
            }
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsDirectField(string content, Node node, int fieldIndex)
    {
        var braces = 0;
        var brackets = 0;
        for (var i = node.Start; i < fieldIndex; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i)
            {
                if (skipped > fieldIndex) return false;
                i = skipped - 1;
                continue;
            }
            if (content[i] == '{') braces++;
            else if (content[i] == '}') braces--;
            else if (content[i] == '[') brackets++;
            else if (content[i] == ']') brackets--;
        }
        return braces == 1 && brackets == 0;
    }

    private static string SetFilename(string content, Node node, string path, string newline, out bool changed)
    {
        var value = DirectFieldMatches(content, node, "filename", StringValue).FirstOrDefault()?.Groups[1];
        var normalized = path.Replace('\\', '/');
        if (value is not null && value.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            changed = false;
            return content;
        }
        changed = true;
        return value is not null
            ? content.Remove(value.Index, value.Length).Insert(value.Index, normalized)
            : content.Insert(node.Start + 1, newline + "filename = \"" + normalized + "\"");
    }

    private static string InsertIntoArray(string content, int open, int close, string node, string newline)
    {
        // Insert after the last element, never inside a trailing comment.
        var insertAt = open + 1;
        var last = '\0';
        for (var i = open + 1; i < close; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i)
            {
                if (content[i] == '"') { insertAt = skipped; last = '"'; }
                i = skipped - 1;
                continue;
            }
            if (char.IsWhiteSpace(content[i])) continue;
            insertAt = i + 1;
            last = content[i];
        }
        var separator = insertAt > open + 1 && last != ',' ? "," : string.Empty;
        return content.Insert(insertAt, separator + newline + node + ",");
    }

    private static int FindMatching(string content, int open, char openChar, char closeChar)
    {
        if (open < 0 || open >= content.Length || content[open] != openChar) return -1;
        var depth = 0;
        for (var i = open; i < content.Length; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i) { i = skipped - 1; continue; }
            if (content[i] == openChar) depth++;
            else if (content[i] == closeChar && --depth == 0) return i;
        }
        return -1;
    }

    private static int SkipIgnored(string content, int i)
    {
        if (content[i] == '"')
        {
            if (i + 2 < content.Length && content.AsSpan(i, 3).SequenceEqual("\"\"\""))
            {
                var end = content.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                return end < 0 ? content.Length : end + 3;
            }
            for (var j = i + 1; j < content.Length; j++)
            {
                if (content[j] == '\\') { j++; continue; }
                if (content[j] == '"') return j + 1;
            }
            return content.Length;
        }
        if (content[i] == '/' && i + 1 < content.Length && content[i + 1] == '/')
        {
            var end = content.IndexOf('\n', i + 2);
            return end < 0 ? content.Length : end + 1;
        }
        if (content[i] == '/' && i + 1 < content.Length && content[i + 1] == '*')
        {
            var end = content.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return end < 0 ? content.Length : end + 2;
        }
        return i;
    }

    private static bool IsIgnoredAt(string content, int position)
    {
        for (var i = 0; i <= position && i < content.Length; i++)
        {
            var next = SkipIgnored(content, i);
            if (next <= i) continue;
            if (next > position) return true;
            i = next - 1;
        }
        return false;
    }
}

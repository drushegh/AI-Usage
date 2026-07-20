namespace AIUsageTray;

/// <summary>
/// Fair-share line budgeting for the tray tooltip (task T12; review P2-17). Given several provider lines and
/// a hard character cap, it guarantees that NO whole line is dropped when another is long: every line keeps
/// at least a floor of characters (abbreviated with an ellipsis if needed) before any remaining budget is
/// handed out. This is what stops a long Claude line from truncating the entire Codex line away — an
/// omission that would read as implied-safe, the exact failure the HARD RULE bans. Pure and
/// side-effect-free so it is unit-testable without WinForms.
/// </summary>
public static class TooltipBudget
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Join <paramref name="lines"/> with newlines within <paramref name="maxLength"/> characters, giving
    /// each line at least <paramref name="perLineFloor"/> characters (or its full length if shorter) before
    /// distributing any remaining budget round-robin. The result never exceeds <paramref name="maxLength"/>,
    /// and when the budget allows even one character per line, no line is dropped entirely.
    /// </summary>
    public static string Fit(IReadOnlyList<string> lines, int maxLength, int perLineFloor)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (maxLength <= 0 || lines.Count == 0)
        {
            return string.Empty;
        }

        int n = lines.Count;
        int separatorCount = n - 1;

        long totalRaw = separatorCount;
        foreach (var line in lines)
        {
            totalRaw += line.Length;
        }

        // Everything already fits — emit verbatim.
        if (totalRaw <= maxLength)
        {
            return string.Join('\n', lines);
        }

        int contentBudget = maxLength - separatorCount;
        if (contentBudget < n)
        {
            // Fewer characters than lines once separators are reserved — nothing fair to do; fall back to a
            // plain global truncation of the joined text.
            return Truncate(string.Join('\n', lines), maxLength);
        }

        // Guaranteed per-line floor, never more than the fair equal share when the budget is tight.
        int floor = Math.Min(Math.Max(perLineFloor, 0), contentBudget / n);

        var allowance = new int[n];
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            allowance[i] = Math.Min(lines[i].Length, floor);
            used += allowance[i];
        }

        // Distribute what remains, round-robin, only to lines that still want more.
        int remaining = contentBudget - used;
        bool progressed = true;
        while (remaining > 0 && progressed)
        {
            progressed = false;
            for (int i = 0; i < n && remaining > 0; i++)
            {
                if (allowance[i] < lines[i].Length)
                {
                    allowance[i]++;
                    remaining--;
                    progressed = true;
                }
            }
        }

        var parts = new string[n];
        for (int i = 0; i < n; i++)
        {
            parts[i] = Truncate(lines[i], allowance[i]);
        }

        return string.Join('\n', parts);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return maxLength == 1
            ? Ellipsis
            : string.Concat(text.AsSpan(0, maxLength - 1), Ellipsis);
    }
}

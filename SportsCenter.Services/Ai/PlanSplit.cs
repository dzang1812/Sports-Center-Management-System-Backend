namespace SportsCenter.Services.Ai;

/// <summary>
/// Decides the split of a weekly plan from the chosen training days, so the model is told the split instead of guessing it.
/// Evenly spaced days -> Full Body; even count -> Upper/Lower; odd count -> Upper/Lower with a Full Body last day when it follows a rest day.
/// </summary>
public static class PlanSplit
{
    public const string Full = "Full Body", Upper = "Upper Body", Lower = "Lower Body";

    /// <summary>Returns (day, split) in week order Monday..Sunday, or null when fewer than 2 or more than 6 valid days are given.</summary>
    public static List<(DayOfWeek Day, string Split)>? For(IEnumerable<string> dayNames)
    {
        var days = dayNames.Select(n => Enum.TryParse<DayOfWeek>(n?.Trim(), true, out var d) ? (DayOfWeek?)d : null)
            .Where(d => d is not null).Select(d => d!.Value).Distinct()
            .OrderBy(d => ((int)d + 6) % 7).ToList(); // Monday first
        var n = days.Count;
        if (n is < 2 or > 6) return null;

        var idx = days.Select(d => ((int)d + 6) % 7).ToList();
        var gaps = Enumerable.Range(0, n).Select(i => ((idx[(i + 1) % n] - idx[i]) + 7) % 7).ToList(); // wraps from Sunday to Monday
        var even = gaps.Min() >= 2 && gaps.Max() - gaps.Min() <= 1; // at least one rest day between sessions, spread evenly

        var splits = new List<string>();
        for (var i = 0; i < n; i++) splits.Add(even ? Full : i % 2 == 0 ? Upper : Lower);
        // Odd count: the last day is Full Body only if a rest day comes right before it.
        if (!even && n % 2 == 1 && gaps[n - 2] >= 2) splits[n - 1] = Full;

        return days.Zip(splits).ToList();
    }
}

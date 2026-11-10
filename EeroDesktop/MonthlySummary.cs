using System.Globalization;
using EeroDesktop.Pages;

namespace EeroDesktop;

/// <summary>
/// A short month-in-review notification built from <see cref="HistoryStore"/>: average speed, outages and DNS
/// benchmarks for the previous calendar month. Sent once per month by the background monitor (Settings can turn it off).
/// </summary>
public static class MonthlySummary
{
    public sealed record Summary(string Title, string Body);

    public static string MonthKey(DateTime local) => local.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// The summary for one network and month (local time), or null when there's nothing to report.
    /// <paramref name="trackingSinceUtc"/> is when eero Desktop started watching for outages (before then, outages are unknown).
    /// </summary>
    public static Summary? Build(string networkId, string networkName, int year, int month, DateTime trackingSinceUtc)
    {
        var startLocal = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Local);
        var start = startLocal.ToUniversalTime();
        var end = startLocal.AddMonths(1).ToUniversalTime();
        var monthName = startLocal.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        var lines = new List<string>();

        var tests = HistoryStore.SpeedTests(networkId, start).Where(t => t.Utc < end).ToList();
        if (tests.Count > 0)
            lines.Add($"Speed: {tests.Average(t => t.DownMbps):N0} Mbps down, {tests.Average(t => t.UpMbps):N0} up on average "
                      + $"({tests.Count} test{(tests.Count == 1 ? "" : "s")}; slowest {tests.Min(t => t.DownMbps):N0} down).");

        if (trackingSinceUtc != default && trackingSinceUtc < end)
        {
            var outages = HistoryStore.Outages(networkId, start).Where(o => o.Kind == "internet" && o.StartUtc < end).ToList();
            // Count only the part of each outage inside this month.
            TimeSpan InMonth(HistoryStore.Outage o)
            {
                var from = o.StartUtc < start ? start : o.StartUtc;
                var to = o.EndUtc ?? DateTime.UtcNow;
                if (to > end) to = end;
                return to > from ? to - from : TimeSpan.Zero;
            }
            var down = outages.Aggregate(TimeSpan.Zero, (sum, o) => sum + InMonth(o));
            var partial = trackingSinceUtc > start ? $" (watched since {trackingSinceUtc.ToLocalTime():MMM d})" : "";
            lines.Add(outages.Count == 0 ? $"Internet: no outages seen{partial}."
                : $"Internet: {outages.Count} outage{(outages.Count == 1 ? "" : "s")}, {HistoryPage.Duration(down)} down in total{partial}.");
            var eeroProblems = HistoryStore.Outages(networkId, start).Count(o => o.Kind == "eero" && o.StartUtc < end);
            if (eeroProblems > 0) lines.Add($"eeros: {eeroProblems} problem{(eeroProblems == 1 ? "" : "s")} reported.");
        }

        var runs = HistoryStore.DnsRuns(networkId, start).Where(r => r.Utc < end).ToList();
        if (runs.Count > 0)
        {
            var winners = runs.Select(r => r.Providers.Where(p => p.Ok).MinBy(p => p.MedianMs)?.Name).Where(n => n is not null)
                .GroupBy(n => n).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            lines.Add($"DNS: {runs.Count} benchmark{(runs.Count == 1 ? "" : "s")}" + (winners is null ? "." : $"; fastest was usually {winners.Replace(" (eero automatic)", "")}."));
        }

        return lines.Count == 0 ? null : new Summary($"{monthName} on {networkName}", string.Join("\n", lines));
    }
}

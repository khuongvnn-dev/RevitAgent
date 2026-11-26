using System;
using System.IO;
using System.Windows.Media;

namespace RevitAgent.Host.Models;

public sealed class RunHistoryItem
{
    public string Id { get; init; } = string.Empty;

    public string Scenario { get; init; } = string.Empty;

    public string Trigger { get; init; } = "manual";

    public string Verdict { get; init; } = "UNKNOWN";

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public string RunDir { get; init; } = string.Empty;

    public string? ReportHtml { get; init; }

    public string? ReportMd { get; init; }

    public bool HasReportHtml => !string.IsNullOrWhiteSpace(ReportHtml) && File.Exists(ReportHtml);

    public string FormattedStarted => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string DateGroup => StartedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string DurationText
    {
        get
        {
            if (EndedAt.HasValue)
            {
                var dur = EndedAt.Value - StartedAt;
                if (dur.TotalMinutes >= 1)
                {
                    return $"{Math.Floor(dur.TotalMinutes)}m {dur.Seconds}s";
                }
                return $"{Math.Round(dur.TotalSeconds, 1)}s";
            }
            return "--";
        }
    }

    public Brush VerdictBrush
    {
        get
        {
            var v = (Verdict ?? string.Empty).ToUpperInvariant();
            return v switch
            {
                "PASS" => new SolidColorBrush(Color.FromRgb(16, 185, 129)),       // Emerald Green #10B981
                "FAIL" => new SolidColorBrush(Color.FromRgb(239, 68, 68)),        // Red #EF4444
                "SUSPECT" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),    // Amber #F59E0B
                "ERROR" => new SolidColorBrush(Color.FromRgb(239, 68, 68)),      // Red #EF4444
                "RUNNING" => new SolidColorBrush(Color.FromRgb(59, 130, 246)),    // Blue #3B82F6
                _ => new SolidColorBrush(Color.FromRgb(107, 114, 128)),           // Gray #6B7280
            };
        }
    }
}

using System.Globalization;
using OpenPayments.Console.Data;

namespace OpenPayments.Console.Presentation;

/// <summary>Formatting and status words for the home dashboard.</summary>
public static class DashboardPresentation
{
    public static string Rate(double? perSecond) =>
        perSecond is { } value ? value.ToString("0.##", CultureInfo.InvariantCulture) + " /s" : "—";

    public static string Latency(double? seconds) =>
        seconds is { } value ? (value * 1000).ToString("0.#", CultureInfo.InvariantCulture) + " ms" : "—";

    public static string Count(long? count) =>
        count is { } value ? value.ToString("N0", CultureInfo.InvariantCulture) : "—";

    public static string AgeSeconds(double? seconds) => seconds switch
    {
        null => "—",
        < 60 => seconds.Value.ToString("0", CultureInfo.InvariantCulture) + " s",
        _ => AttentionPresentation.Duration(TimeSpan.FromSeconds(seconds.Value)),
    };

    public static string Age(DateTimeOffset at, DateTimeOffset now) =>
        AttentionPresentation.Duration(now - at);

    public static string CheckText(CorrectnessCheck check) => check.Issues switch
    {
        0 => "Pass",
        1 => "1 issue",
        _ => check.Issues.ToString("N0", CultureInfo.InvariantCulture) + " issues",
    };

    public static string CheckTone(CorrectnessCheck check) => check.Issues == 0 ? "tone-success" : "tone-warning";

    public static string CheckIcon(CorrectnessCheck check) => check.Issues == 0 ? "check" : "alert";

    public static string LifecycleLabel(string state) => StatePresentation.For(state).Label;

    public static string LifecycleTone(string state) => StatePresentation.For(state).ToneClass;

    public static string LifecycleIcon(string state) => StatePresentation.For(state).Icon;
}

using System.Reflection;

namespace MicMixer.Diagnostics;

/// <summary>
/// Resolves the app version stamped by the release pipeline (year.month.sequence).
/// Local builds have no stamped version and show "dev" instead.
/// </summary>
internal static class AppVersion
{
    public static Version? Current { get; } = ResolveCurrent();

    public static string DisplayText { get; } = Compute();

    private static string Compute()
    {
        var version = Current;

        if (version is null || version.Major < 2000)
        {
            return "dev";
        }

        return $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private static Version? ResolveCurrent() => Assembly.GetExecutingAssembly().GetName().Version;
}

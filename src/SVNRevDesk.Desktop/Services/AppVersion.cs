using System.Reflection;

namespace SVNRevDesk.Desktop.Services;

public static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    public static string Display => $"版本 {Current}";
}

using System.Reflection;

namespace TwitchDropsBot.Core
{
    public static class VersionHelper
    {
        public static string GetCoreVersion()
        {
            var assembly = typeof(VersionHelper).Assembly;
            var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            return Trim(infoVersion) ?? assembly.GetName().Version?.ToString(3) ?? "Unknown";
        }

        public static string GetUIVersion(Assembly uiAssembly)
        {
            var infoVersion = uiAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            return Trim(infoVersion) ?? uiAssembly.GetName().Version?.ToString(3) ?? "Unknown";
        }

        // Display a clean version (e.g. "1.2.8") instead of the full MinVer string
        // ("1.2.8-dbee9a0.2+sha...") in the window title.
        private static string? Trim(string? informationalVersion)
        {
            if (string.IsNullOrEmpty(informationalVersion))
            {
                return informationalVersion;
            }

            var cutIndex = informationalVersion.IndexOfAny(new[] { '-', '+' });
            return cutIndex > 0 ? informationalVersion.Substring(0, cutIndex) : informationalVersion;
        }
    }
}

using System.Reflection;

namespace Nes.Debug.Emulator;

internal static class EmulatorBuildInfo
{
    public static string Version { get; } =
        typeof(EmulatorBuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(EmulatorBuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}

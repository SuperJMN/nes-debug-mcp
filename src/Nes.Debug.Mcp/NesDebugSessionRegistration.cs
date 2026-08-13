using Microsoft.Extensions.DependencyInjection;
using Nes.Debug.Core;
using Nes.Debug.Emulator;

namespace Nes.Debug.Mcp;

/// <summary>
/// Registers the <see cref="INesDebugSession"/> backend selected by the
/// <c>NES_MCP_EMULATOR_BACKEND</c> environment variable ("auto", "aprnes", or "adnes").
/// </summary>
public static class NesDebugSessionRegistration
{
    public static IServiceCollection AddNesDebugSession(this IServiceCollection services, string? backend)
    {
        switch ((backend ?? "auto").Trim().ToLowerInvariant())
        {
            case "auto":
            case "aprnes":
                return AddAprNesSession(services);

            case "adnes":
                services.AddSingleton<ManagedNesDebugSession>();
                services.AddSingleton<INesDebugSession>(provider =>
                    new SynchronizedNesDebugSession(provider.GetRequiredService<ManagedNesDebugSession>()));
                return services;

            default:
                throw new InvalidOperationException(
                    $"Unsupported NES emulator backend '{backend}'. " +
                    "Leave NES_MCP_EMULATOR_BACKEND unset, use legacy alias 'auto' or 'aprnes', " +
                    "or use the temporary 'adnes' fallback.");
        }
    }

    private static IServiceCollection AddAprNesSession(IServiceCollection services)
    {
        services.AddSingleton<AprNesDebugSession>();
        services.AddSingleton<INesDebugSession>(provider =>
            new SynchronizedNesDebugSession(provider.GetRequiredService<AprNesDebugSession>()));
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Nes.Debug.Core;
using Nes.Debug.Emulator;

namespace Nes.Debug.Mcp;

/// <summary>
/// Registers the <see cref="INesDebugSession"/> backend selected by the
/// <c>NES_MCP_EMULATOR_BACKEND</c> environment variable ("auto" or "aprnes").
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
                throw new InvalidOperationException(
                    "NES emulator backend 'adnes' has been removed. " +
                    "AprNes is the only supported backend. " +
                    "Leave NES_MCP_EMULATOR_BACKEND unset, or use 'auto' or 'aprnes'.");

            default:
                throw new InvalidOperationException(
                    $"Unsupported NES emulator backend '{backend}'. " +
                    "AprNes is the only supported backend. " +
                    "Leave NES_MCP_EMULATOR_BACKEND unset, or use 'auto' or 'aprnes'.");
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

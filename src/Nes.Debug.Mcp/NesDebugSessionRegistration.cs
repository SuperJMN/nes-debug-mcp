using Microsoft.Extensions.DependencyInjection;
using Nes.Debug.Core;
using Nes.Debug.Emulator;

namespace Nes.Debug.Mcp;

/// <summary>
/// Registers the <see cref="INesDebugSession"/> backend selected by the
/// <c>NES_MCP_EMULATOR_BACKEND</c> environment variable ("auto", "adnes", or "aprnes").
/// </summary>
public static class NesDebugSessionRegistration
{
    public static IServiceCollection AddNesDebugSession(this IServiceCollection services, string? backend)
    {
        switch ((backend ?? "auto").Trim().ToLowerInvariant())
        {
            case "auto":
                services.AddSingleton<ManagedNesDebugSession>();
                services.AddSingleton<AprNesDebugSession>();
                // AutoNesDebugSession must be constructed with the two concrete backends. Registering
                // it as a resolved service whose constructor takes INesDebugSession created a circular
                // dependency: INesDebugSession -> SynchronizedNesDebugSession(AutoNesDebugSession) ->
                // AutoNesDebugSession -> INesDebugSession (again). The DI container takes a
                // per-singleton lock and the re-entrant resolution deadlocked, so every MCP tool call
                // that injected INesDebugSession hung (MCP -32001 request timeout).
                services.AddSingleton<INesDebugSession>(provider =>
                    new SynchronizedNesDebugSession(
                        new AutoNesDebugSession(
                            provider.GetRequiredService<ManagedNesDebugSession>(),
                            provider.GetRequiredService<AprNesDebugSession>())));
                return services;

            case "adnes":
                services.AddSingleton<ManagedNesDebugSession>();
                services.AddSingleton<INesDebugSession>(provider =>
                    new SynchronizedNesDebugSession(provider.GetRequiredService<ManagedNesDebugSession>()));
                return services;

            case "aprnes":
                services.AddSingleton<AprNesDebugSession>();
                services.AddSingleton<INesDebugSession>(provider =>
                    new SynchronizedNesDebugSession(provider.GetRequiredService<AprNesDebugSession>()));
                return services;

            default:
                throw new InvalidOperationException(
                    $"Unsupported NES emulator backend '{backend}'. Use 'auto', 'adnes', or 'aprnes'.");
        }
    }
}

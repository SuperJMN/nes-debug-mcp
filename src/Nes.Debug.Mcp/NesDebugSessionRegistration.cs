using Microsoft.Extensions.DependencyInjection;
using Nes.Debug.Core;
using Nes.Debug.Emulator;

namespace Nes.Debug.Mcp;

/// <summary>
/// Registers the synchronized AprNes <see cref="INesDebugSession"/>.
/// </summary>
public static class NesDebugSessionRegistration
{
    public static IServiceCollection AddNesDebugSession(this IServiceCollection services)
    {
        services.AddSingleton<AprNesDebugSession>();
        services.AddSingleton<INesDebugSession>(provider =>
            new SynchronizedNesDebugSession(provider.GetRequiredService<AprNesDebugSession>()));
        return services;
    }
}

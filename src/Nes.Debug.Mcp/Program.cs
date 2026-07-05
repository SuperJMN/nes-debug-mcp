using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nes.Debug.Core;
using Nes.Debug.Emulator;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var emulatorBackend = Environment.GetEnvironmentVariable("NES_MCP_EMULATOR_BACKEND") ?? "auto";
switch (emulatorBackend.Trim().ToLowerInvariant())
{
    case "auto":
        builder.Services.AddSingleton<ManagedNesDebugSession>();
        builder.Services.AddSingleton<AprNesDebugSession>();
        builder.Services.AddSingleton<AutoNesDebugSession>();
        builder.Services.AddSingleton<INesDebugSession>(provider =>
            new SynchronizedNesDebugSession(provider.GetRequiredService<AutoNesDebugSession>()));
        break;

    case "adnes":
        builder.Services.AddSingleton<ManagedNesDebugSession>();
        builder.Services.AddSingleton<INesDebugSession>(provider =>
            new SynchronizedNesDebugSession(provider.GetRequiredService<ManagedNesDebugSession>()));
        break;

    case "aprnes":
        builder.Services.AddSingleton<AprNesDebugSession>();
        builder.Services.AddSingleton<INesDebugSession>(provider =>
            new SynchronizedNesDebugSession(provider.GetRequiredService<AprNesDebugSession>()));
        break;

    default:
        throw new InvalidOperationException(
            $"Unsupported NES emulator backend '{emulatorBackend}'. Use 'auto', 'adnes', or 'aprnes'.");
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

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

builder.Services.AddSingleton<ManagedNesDebugSession>();
builder.Services.AddSingleton<INesDebugSession>(provider =>
    new SynchronizedNesDebugSession(provider.GetRequiredService<ManagedNesDebugSession>()));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

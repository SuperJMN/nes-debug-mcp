using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nes.Debug.Mcp;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var emulatorBackend = Environment.GetEnvironmentVariable("NES_MCP_EMULATOR_BACKEND") ?? "auto";
builder.Services.AddNesDebugSession(emulatorBackend);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

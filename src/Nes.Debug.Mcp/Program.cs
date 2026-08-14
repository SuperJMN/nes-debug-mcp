using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nes.Debug.Mcp;

// StdioServerTransport writes JSON-RPC through the raw Console.OpenStandardOutput stream.
// Permanently redirect Console.Write* so inherited emulator diagnostics use stderr instead.
Console.SetOut(Console.Error);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddNesDebugSession();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

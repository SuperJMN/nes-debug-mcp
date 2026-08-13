using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Nes.Debug.Tests;

[Collection(NesDebugSessionCollection.Name)]
public sealed class PackagedAprNesStdioSmokeTests
{
    [Fact]
    public async Task Packaged_stdio_server_loads_and_advances_nrom_with_json_only_stdout()
    {
        using var workspace = new TemporaryDirectory();
        var repositoryRoot = FindRepositoryRoot();
        var packageDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "packages")).FullName;
        var extractedDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "tool")).FullName;
        var artifactsDirectory = Path.Combine(workspace.Path, "artifacts");
        var projectPath = Path.Combine(repositoryRoot, "src", "Nes.Debug.Mcp", "Nes.Debug.Mcp.csproj");
        var configuration = GetBuildConfiguration();

        var pack = await RunProcessAsync(
            "dotnet",
            repositoryRoot,
            [
                "pack",
                projectPath,
                "--configuration", configuration,
                "--disable-build-servers",
                "--artifacts-path", artifactsDirectory,
                "--output", packageDirectory,
                "-p:PackageVersion=0.0.0-stdio-smoke",
            ],
            TimeSpan.FromSeconds(90));
        Assert.True(pack.ExitCode == 0, $"dotnet pack failed.\nstdout:\n{pack.Stdout}\nstderr:\n{pack.Stderr}");

        var package = Assert.Single(Directory.GetFiles(packageDirectory, "Nes.Mcp.*.nupkg"));
        ZipFile.ExtractToDirectory(package, extractedDirectory);
        var serverAssembly = Directory.GetFiles(extractedDirectory, "Nes.Mcp.dll", SearchOption.AllDirectories).Single();

        var fixture = NromTestRomBuilder.CreateProgram(
        [
            0xE6, 0x00,       // INC $00
            0x4C, 0x00, 0x80, // JMP $8000
        ], prgRomBanks: 1, chrRomBanks: 1);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);

        using var server = StartServer(serverAssembly, repositoryRoot);
        var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { },
                        clientInfo = new { name = "nes-mcp-stdio-smoke", version = "1.0" },
                    },
                });
            var initialize = await ReadResponseAsync(server, 1, TimeSpan.FromSeconds(10));
            Assert.False(initialize.TryGetProperty("error", out _), initialize.GetRawText());

            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                });
            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/call",
                    @params = new
                    {
                        name = "load_rom",
                        arguments = new { path = rom.Path },
                    },
                });
            var load = await ReadResponseAsync(server, 2, TimeSpan.FromSeconds(10));
            AssertToolResponseContains(load, "\"mapper\":0");

            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "tools/call",
                    @params = new
                    {
                        name = "get_state",
                        arguments = new { },
                    },
                });
            var state = await ReadResponseAsync(server, 3, TimeSpan.FromSeconds(10));
            AssertToolResponseContains(state, "\"backend\":\"AprNes\"");

            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    id = 4,
                    method = "tools/call",
                    @params = new
                    {
                        name = "run_frame",
                        arguments = new { count = 1 },
                    },
                });
            var frame = await ReadResponseAsync(server, 4, TimeSpan.FromSeconds(10));
            AssertToolResponseContains(frame, "\"framesRun\":1");

            server.StandardInput.Close();
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var trailingStdout = await server.StandardOutput.ReadToEndAsync();
            AssertJsonLines(trailingStdout);
            Assert.True(server.ExitCode == 0, $"MCP server exited with {server.ExitCode}.\nstderr:\n{await stderr}");
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync();
            }
        }
    }

    private static Process StartServer(string serverAssembly, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(serverAssembly);
        startInfo.Environment["NES_MCP_EMULATOR_BACKEND"] = "aprnes";
        var process = Process.Start(startInfo);
        return process ?? throw new InvalidOperationException("Failed to start the packaged MCP server.");
    }

    private static async Task SendAsync(Process server, object message)
    {
        await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await server.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadResponseAsync(Process server, int expectedId, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            var line = await server.StandardOutput.ReadLineAsync(cancellation.Token);
            Assert.False(line is null, $"MCP server exited before responding to request {expectedId}.");
            var root = ParseJsonLine(line!);
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expectedId)
            {
                return root;
            }
        }
    }

    private static JsonElement ParseJsonLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Assert.Fail($"MCP stdout contained a non-JSON line: {line}\n{ex.Message}");
            throw;
        }
    }

    private static void AssertJsonLines(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            _ = ParseJsonLine(line);
        }
    }

    private static void AssertToolResponseContains(JsonElement response, string expected)
    {
        Assert.False(response.TryGetProperty("error", out _), response.GetRawText());
        var content = response.GetProperty("result").GetProperty("content");
        var text = Assert.Single(content.EnumerateArray()).GetProperty("text").GetString();
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nes-debug-mcp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the NesMcp repository root.");
    }

    private static string GetBuildConfiguration()
    {
        var targetFrameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        return targetFrameworkDirectory.Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine the active test build configuration.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nes-mcp-stdio-smoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

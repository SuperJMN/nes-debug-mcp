using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Nes.Debug.Tests;

[Collection(NesDebugSessionCollection.Name)]
public sealed class PackagedAprNesStdioSmokeTests
{
    [Fact]
    public async Task Packaged_release_stdio_server_runs_default_aprnes_advanced_workflow_with_json_only_stdout()
    {
        using var workspace = new TemporaryDirectory();
        var repositoryRoot = FindRepositoryRoot();
        var packageDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "packages")).FullName;
        var extractedDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "tool")).FullName;
        var artifactsDirectory = Path.Combine(workspace.Path, "artifacts");
        var projectPath = Path.Combine(repositoryRoot, "src", "Nes.Debug.Mcp", "Nes.Debug.Mcp.csproj");
        const string configuration = "Release";

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
        using (var archive = ZipFile.OpenRead(package))
        {
            Assert.DoesNotContain(
                archive.Entries,
                entry => entry.FullName.Contains("adnes", StringComparison.OrdinalIgnoreCase));
        }

        ZipFile.ExtractToDirectory(package, extractedDirectory);
        var serverAssembly = Directory.GetFiles(extractedDirectory, "Nes.Mcp.dll", SearchOption.AllDirectories).Single();

        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2000, 0x80));
        program.AddRange(
        [
            0xE6, 0x00,       // INC $00
            0x4C, 0x00, 0x80, // JMP $8000
        ]);
        var fixture = NromTestRomBuilder.CreateProgram(program.ToArray(), prgRomBanks: 1, chrRomBanks: 1);
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
            var statePayload = ReadToolPayload(state);
            Assert.Equal("AprNes", statePayload.GetProperty("backend").GetString());
            Assert.False(string.IsNullOrWhiteSpace(statePayload.GetProperty("backendVersion").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(statePayload.GetProperty("serverVersion").GetString()));
            Assert.Equal(1024, statePayload.GetProperty("debugCycleLimit").GetInt32());

            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    id = 4,
                    method = "tools/call",
                    @params = new
                    {
                        name = "step_instruction",
                        arguments = new { count = 1 },
                    },
                });
            var step = ReadToolPayload(await ReadResponseAsync(server, 4, TimeSpan.FromSeconds(10)));
            Assert.Equal(1, step.GetProperty("instructionsRun").GetInt32());

            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    id = 5,
                    method = "tools/call",
                    @params = new
                    {
                        name = "run_frame",
                        arguments = new { count = 1 },
                    },
                });
            var frame = ReadToolPayload(await ReadResponseAsync(server, 5, TimeSpan.FromSeconds(10)));
            Assert.Equal(1, frame.GetProperty("framesRun").GetInt32());

            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    id = 6,
                    method = "tools/call",
                    @params = new
                    {
                        name = "trace_ppu_register_writes",
                        arguments = new
                        {
                            frameCount = 1,
                            maxEvents = 4,
                            registers = new[] { "PPUCTRL", "PPUSCROLL", "PPUADDR", "PPUDATA" },
                            buttons = Array.Empty<string>(),
                        },
                    },
                });
            var trace = ReadToolPayload(await ReadResponseAsync(server, 6, TimeSpan.FromSeconds(10)));
            Assert.Equal(1, trace.GetProperty("framesRun").GetInt32());
            Assert.Equal("framesComplete", trace.GetProperty("stopReason").GetString());
            Assert.InRange(trace.GetProperty("eventCount").GetInt32(), 1, 4);
            Assert.Equal(trace.GetProperty("eventCount").GetInt32(), trace.GetProperty("events").GetArrayLength());

            await SendAsync(server,
                new
                {
                    jsonrpc = "2.0",
                    id = 7,
                    method = "tools/call",
                    @params = new
                    {
                        name = "observe_execution",
                        arguments = new
                        {
                            frameCount = 1,
                            buttons = Array.Empty<string>(),
                            memoryProbes = new[] { new { address = "0x0000", length = 1 } },
                            includePpuState = true,
                            tracePpuWrites = true,
                            maxPpuEvents = 4,
                            ppuRegisters = new[] { "PPUCTRL", "PPUSCROLL", "PPUADDR", "PPUDATA" },
                        },
                    },
                });
            var observation = ReadToolPayload(await ReadResponseAsync(server, 7, TimeSpan.FromSeconds(10)));
            Assert.Equal(1, observation.GetProperty("framesRun").GetInt32());
            Assert.Equal("framesComplete", observation.GetProperty("stopReason").GetString());
            Assert.Single(observation.GetProperty("frames").EnumerateArray());
            Assert.Equal(4, observation.GetProperty("initialNametables").GetProperty("nametables").GetArrayLength());
            Assert.Equal(4, observation.GetProperty("finalNametables").GetProperty("nametables").GetArrayLength());
            Assert.Equal(600, observation.GetProperty("limits").GetProperty("maxFrames").GetInt32());
            Assert.InRange(observation.GetProperty("ppuEventCount").GetInt32(), 1, 4);
            Assert.Equal(
                observation.GetProperty("ppuEventCount").GetInt32(),
                observation.GetProperty("ppuEvents").GetArrayLength());

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

        using var retiredBackend = StartServerWithRetiredBackend(serverAssembly, repositoryRoot);
        retiredBackend.StandardInput.Close();
        var retiredStdout = retiredBackend.StandardOutput.ReadToEndAsync();
        var retiredStderr = retiredBackend.StandardError.ReadToEndAsync();
        try
        {
            await retiredBackend.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!retiredBackend.HasExited)
            {
                retiredBackend.Kill(entireProcessTree: true);
                await retiredBackend.WaitForExitAsync();
            }
        }

        var rejectedStdout = await retiredStdout;
        var rejection = await retiredStderr;
        Assert.NotEqual(0, retiredBackend.ExitCode);
        AssertJsonLines(rejectedStdout);
        Assert.Contains("backend 'adnes' has been removed", rejection, StringComparison.Ordinal);
        Assert.Contains("AprNes is the only supported backend", rejection, StringComparison.Ordinal);
        Assert.Contains("unset", rejection, StringComparison.Ordinal);
        Assert.Contains("'auto'", rejection, StringComparison.Ordinal);
        Assert.Contains("'aprnes'", rejection, StringComparison.Ordinal);
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
        startInfo.Environment.Remove("NES_MCP_EMULATOR_BACKEND");
        var process = Process.Start(startInfo);
        return process ?? throw new InvalidOperationException("Failed to start the packaged MCP server.");
    }

    private static Process StartServerWithRetiredBackend(string serverAssembly, string workingDirectory)
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
        startInfo.Environment["NES_MCP_EMULATOR_BACKEND"] = "adnes";
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

    private static JsonElement ReadToolPayload(JsonElement response)
    {
        Assert.False(response.TryGetProperty("error", out _), response.GetRawText());
        var content = response.GetProperty("result").GetProperty("content");
        var text = Assert.Single(content.EnumerateArray()).GetProperty("text").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        using var document = JsonDocument.Parse(text!);
        var payload = document.RootElement.Clone();
        Assert.False(payload.TryGetProperty("error", out _), payload.GetRawText());
        return payload;
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

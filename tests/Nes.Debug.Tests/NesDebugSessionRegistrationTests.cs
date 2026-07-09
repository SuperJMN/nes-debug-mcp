using Microsoft.Extensions.DependencyInjection;
using Nes.Debug.Core;
using Nes.Debug.Emulator;
using Nes.Debug.Mcp;

namespace Nes.Debug.Tests;

public sealed class NesDebugSessionRegistrationTests
{
    // Regression: the "auto" backend previously registered AutoNesDebugSession as a resolved service
    // whose constructor took INesDebugSession, creating a circular dependency
    // (INesDebugSession -> Synchronized(Auto) -> Auto -> INesDebugSession). The DI container took a
    // per-singleton lock and the re-entrant resolution deadlocked, so every MCP tool call that
    // injected INesDebugSession hung with an MCP -32001 request timeout. Resolving must complete
    // promptly and not deadlock.
    [Theory]
    [InlineData("auto")]
    [InlineData("adnes")]
    [InlineData("aprnes")]
    [InlineData(null)]
    public async Task Resolving_session_does_not_deadlock(string? backend)
    {
        var services = new ServiceCollection();
        services.AddNesDebugSession(backend);
        using var provider = services.BuildServiceProvider();

        var resolve = Task.Run(() => provider.GetRequiredService<INesDebugSession>());
        var completed = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(completed == resolve, "Resolving INesDebugSession deadlocked.");
        Assert.NotNull(await resolve);
    }

    [Fact]
    public void Unsupported_backend_is_rejected()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddNesDebugSession("nope"));
    }
}

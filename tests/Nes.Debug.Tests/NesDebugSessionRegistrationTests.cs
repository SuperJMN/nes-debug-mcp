using Microsoft.Extensions.DependencyInjection;
using Nes.Debug.Core;
using Nes.Debug.Emulator;
using Nes.Debug.Mcp;

namespace Nes.Debug.Tests;

public sealed class NesDebugSessionRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("aprnes")]
    [InlineData(" AUTO ")]
    [InlineData("AprNes")]
    public async Task Default_aliases_resolve_the_direct_synchronized_aprnes_session_without_deadlocking(string? backend)
    {
        var services = new ServiceCollection();
        services.AddNesDebugSession(backend);
        using var provider = services.BuildServiceProvider();

        var resolve = Task.Run(() => provider.GetRequiredService<INesDebugSession>());
        var completed = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(completed == resolve, "Resolving INesDebugSession deadlocked.");
        var session = await resolve;
        var state = session.GetState();

        Assert.IsType<SynchronizedNesDebugSession>(session);
        Assert.True(state.IsSuccess, state.Error?.Message);
        Assert.Equal("AprNes", state.Value.Backend);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AprNesDebugSession));
    }

    [Fact]
    public void Retired_adnes_backend_is_rejected_immediately_with_migration_guidance()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddNesDebugSession("adnes"));

        Assert.Contains("adnes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("removed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AprNes is the only supported backend", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unset", exception.Message, StringComparison.Ordinal);
        Assert.Contains("auto", exception.Message, StringComparison.Ordinal);
        Assert.Contains("aprnes", exception.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Fact]
    public void Unsupported_backend_is_rejected_immediately_with_actionable_valid_values()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddNesDebugSession("nope"));

        Assert.Contains("nope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unset", exception.Message, StringComparison.Ordinal);
        Assert.Contains("auto", exception.Message, StringComparison.Ordinal);
        Assert.Contains("aprnes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AprNes is the only supported backend", exception.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }
}

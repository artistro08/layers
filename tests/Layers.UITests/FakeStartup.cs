using Layers.Core.Services;

namespace Layers.UITests;

/// An in-memory start at sign-in, so tests never touch the Run key.
internal sealed class FakeStartup(StartupState state) : IStartupRegistration
{
    public Task<StartupState> GetStateAsync() => Task.FromResult(state);

    public Task<StartupState> SetEnabledAsync(bool enabled) => Task.FromResult(state = state with { IsEnabled = enabled });
}

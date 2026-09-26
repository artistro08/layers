using Microsoft.Extensions.Time.Testing;

namespace Layers.Tests.Fakes;

/// Polls a condition, optionally advancing fake time between polls, so async device code can settle.
internal static class Eventually
{
    public static async Task Until(Func<bool> condition, FakeTimeProvider? time = null, int stepMs = 1, int attempts = 1000)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (condition())
            {
                return;
            }

            time?.Advance(TimeSpan.FromMilliseconds(stepMs));
            await Task.Delay(2);
        }

        Assert.Fail("condition not met in time");
    }

    /// Lets queued continuations run without advancing fake time.
    public static Task Settle() => Task.Delay(50);
}

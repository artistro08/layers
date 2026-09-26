namespace Layers.Core.Logic;

/// <summary>
/// How long the HUD stays up.
/// </summary>
/// <remarks>
/// By default the HUD holds for 1.7 s after the last layer change (the user can change it with HUD Show Duration),
/// then slides out over 150 ms.
/// </remarks>
public static class HudTiming
{
    /// <summary>Gets the default for how long the HUD stays open after the last layer change.</summary>
    public static TimeSpan Hold { get; } = TimeSpan.FromMilliseconds(1700);

    /// <summary>Gets how long the exit slide and fade take.</summary>
    public static TimeSpan Exit { get; } = TimeSpan.FromMilliseconds(150);
}

namespace FSTService;

/// <summary>
/// Feature flags controlling which UI features are enabled.
/// Loaded from appsettings.json / environment variables.
/// </summary>
public sealed class FeatureOptions
{
    public const string Section = "Features";

    /// <summary>Item Shop page, navigation links, sort option, pulsing, settings toggles.</summary>
    public bool Shop { get; set; }

    /// <summary>Rivals pages and navigation links.</summary>
    public bool Rivals { get; set; }

    /// <summary>Leaderboards overview and full rankings pages.</summary>
    public bool Leaderboards { get; set; }

    /// <summary>
    /// Compete page — derived from <see cref="Rivals"/> AND <see cref="Leaderboards"/>.
    /// CompetePage links to both rivals and rankings; if either is off, compete is off.
    /// </summary>
    public bool Compete => Rivals && Leaderboards;
}

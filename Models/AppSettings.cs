namespace PetGPT.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public string SelectedPetId { get; set; } = "legacy";
    public Dictionary<string, string> SelectedPackVersions { get; set; } = new(StringComparer.Ordinal);
    public string? ChatHomeUrl { get; set; }
    public PetPlacementSettings PetPlacement { get; set; } = new();
    public ChatWindowSettings ChatWindow { get; set; } = new();
    public bool CompactMode { get; set; } = true;
    public bool ThemesEnabled { get; set; } = true;
    public RoleplaySettings Roleplay { get; set; } = new();
    public bool ReactionsEnabled { get; set; }
    public bool ShowControlMarkers { get; set; }
    public Dictionary<string, PetOptionSettings> PetOptions { get; set; } = new(StringComparer.Ordinal);
    public bool SuspendHiddenBrowser { get; set; }

    public AppSettings Copy() => new()
    {
        SchemaVersion = SchemaVersion,
        SelectedPetId = SelectedPetId,
        SelectedPackVersions = new Dictionary<string, string>(SelectedPackVersions, StringComparer.Ordinal),
        ChatHomeUrl = ChatHomeUrl,
        PetPlacement = PetPlacement.Copy(),
        ChatWindow = ChatWindow.Copy(),
        CompactMode = CompactMode,
        ThemesEnabled = ThemesEnabled,
        Roleplay = Roleplay.Copy(),
        ReactionsEnabled = ReactionsEnabled,
        ShowControlMarkers = ShowControlMarkers,
        PetOptions = PetOptions.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Copy(),
            StringComparer.Ordinal),
        SuspendHiddenBrowser = SuspendHiddenBrowser
    };
}

public sealed class PetPlacementSettings
{
    public string? MonitorId { get; set; }
    public double? XWithinWorkAreaDip { get; set; }
    public double? YWithinWorkAreaDip { get; set; }

    public PetPlacementSettings Copy() => new()
    {
        MonitorId = MonitorId,
        XWithinWorkAreaDip = XWithinWorkAreaDip,
        YWithinWorkAreaDip = YWithinWorkAreaDip
    };
}

public sealed class ChatWindowSettings
{
    public string PlacementMode { get; set; } = "FollowPet";
    public double WidthDip { get; set; } = 500;
    public double HeightDip { get; set; } = 650;
    public string? MonitorId { get; set; }
    public double? XWithinWorkAreaDip { get; set; }
    public double? YWithinWorkAreaDip { get; set; }

    public ChatWindowSettings Copy() => new()
    {
        PlacementMode = PlacementMode,
        WidthDip = WidthDip,
        HeightDip = HeightDip,
        MonitorId = MonitorId,
        XWithinWorkAreaDip = XWithinWorkAreaDip,
        YWithinWorkAreaDip = YWithinWorkAreaDip
    };
}

public sealed class RoleplaySettings
{
    public bool Enabled { get; set; }
    public string ActivationMode { get; set; } = "ReviewThenSend";

    public RoleplaySettings Copy() => new()
    {
        Enabled = Enabled,
        ActivationMode = ActivationMode
    };
}

public sealed class PetOptionSettings
{
    public double Scale { get; set; } = 1;
    public bool ReducedMotion { get; set; }

    public PetOptionSettings Copy() => new()
    {
        Scale = Scale,
        ReducedMotion = ReducedMotion
    };
}

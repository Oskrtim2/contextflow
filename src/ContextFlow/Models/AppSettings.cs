namespace ContextFlow.Models;

/// <summary>
/// User preferences and capsule topology. Everything is stored locally under
/// %LOCALAPPDATA%\ContextFlow\settings.json.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public bool IsDarkTheme { get; set; }
    public bool AutoOptimize { get; set; } = true;

    /// <summary>How hard the local AI optimizes: Conservative, Balanced or Aggressive.</summary>
    public AiProfile AiProfile { get; set; } = AiProfile.Balanced;

    /// <summary>Split-screen layout used when a page is activated.</summary>
    public PageLayout PageLayout { get; set; } = PageLayout.Auto;

    /// <summary>UI language: "en" or "es".</summary>
    public string Language { get; set; } = "en";

    public string ScrollUpHotkey { get; set; } = "Ctrl+E";
    public string ScrollDownHotkey { get; set; } = "Ctrl+F";

    /// <summary>
    /// Opt-in DWM cloaking of windows that belong to inactive capsules.
    /// Disabled by default because it changes the visibility of other apps.
    /// </summary>
    public bool CapsuleIsolation { get; set; }

    public string ActiveCapsuleId { get; set; } = string.Empty;

    public double? BubbleLeft { get; set; }
    public double? BubbleTop { get; set; }

    /// <summary>
    /// Last normal HUD rectangle. Stored in WPF device-independent units so the
    /// window can be restored on whichever physical monitor the user chose.
    /// </summary>
    public double? HudLeft { get; set; }
    public double? HudTop { get; set; }
    public double? HudWidth { get; set; }
    public double? HudHeight { get; set; }
    public bool HudMaximized { get; set; }

    public bool HasCompletedFirstRun { get; set; }

    public List<CapsuleDefinition> Capsules { get; set; } = new();

    public List<string> ProcessExclusions { get; set; } =
    [
        "audiodg",
        "dwm",
        "MsMpEng",
        "SearchIndexer"
    ];

    public void Sanitize()
    {
        Capsules ??= new List<CapsuleDefinition>();
        ProcessExclusions ??= new List<string>();

        // Never let a failed/unarranged window geometry reach JSON. System.Text.Json
        // refuses to serialize NaN and Infinity.
        BubbleLeft = FiniteOrNull(BubbleLeft);
        BubbleTop = FiniteOrNull(BubbleTop);
        HudLeft = FiniteOrNull(HudLeft);
        HudTop = FiniteOrNull(HudTop);
        HudWidth = FiniteOrNull(HudWidth);
        HudHeight = FiniteOrNull(HudHeight);

        if (!Enum.IsDefined(AiProfile))
        {
            AiProfile = AiProfile.Balanced;
        }

        Language = string.Equals(Language, "es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
        ScrollUpHotkey = HotkeyGesture.Parse(ScrollUpHotkey, HotkeyGesture.DefaultScrollUp).Display;
        ScrollDownHotkey = HotkeyGesture.Parse(ScrollDownHotkey, HotkeyGesture.DefaultScrollDown).Display;

        foreach (var capsule in Capsules)
        {
            if (string.IsNullOrWhiteSpace(capsule.Id))
            {
                capsule.Id = Guid.NewGuid().ToString("N");
            }

            capsule.Name = string.IsNullOrWhiteSpace(capsule.Name) ? "Capsule" : capsule.Name.Trim();
            capsule.Accent = string.IsNullOrWhiteSpace(capsule.Accent) ? "#6366F1" : capsule.Accent.Trim();
            capsule.CanvasWidth = Math.Clamp(capsule.CanvasWidth, 320, 100_000);
            capsule.CanvasHeight = Math.Clamp(capsule.CanvasHeight, 240, 100_000);
            capsule.CanvasX = Math.Clamp(capsule.CanvasX, -1_000_000, 1_000_000);
            capsule.CanvasY = Math.Clamp(capsule.CanvasY, -1_000_000, 1_000_000);
        }

        if (!Capsules.Any(c => c.Id == ActiveCapsuleId))
        {
            ActiveCapsuleId = Capsules.FirstOrDefault()?.Id ?? string.Empty;
        }
    }

    private static double? FiniteOrNull(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;
}

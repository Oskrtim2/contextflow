using ContextFlow.Models;

namespace ContextFlow.ViewModels;

public sealed class DisplayItemViewModel
{
    public DisplayItemViewModel(DisplayInfo display)
    {
        Display = display;
    }

    public DisplayInfo Display { get; }
    public int Index => Display.Index;
    public string DeviceName => Display.DeviceName;
    public string Label => Display.Label;
    public string ResolutionLabel => Display.ResolutionLabel;
    public string DetailLabel => Display.DetailLabel;
    public bool IsPrimary => Display.IsPrimary;
    public string Badge => IsPrimary ? "PRIMARY" : "EXTENDED";
}

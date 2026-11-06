using System.Windows.Media;
using ContextFlow.Infrastructure;
using ContextFlow.Models;

namespace ContextFlow.ViewModels;

public sealed class CapsuleItemViewModel : ObservableObject
{
    private int _windowCount;
    private bool _isActive;
    private string _targetDisplayLabel;

    public CapsuleItemViewModel(CapsuleDefinition model, string targetDisplayLabel)
    {
        Model = model;
        _targetDisplayLabel = targetDisplayLabel;
        AccentBrush = ColorUtil.BrushFromHex(model.Accent);
        AccentSoftBrush = ColorUtil.WithAlpha(model.Accent, 42);
    }

    public CapsuleDefinition Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Accent => Model.Accent;
    public Brush AccentBrush { get; }
    public Brush AccentSoftBrush { get; }
    public string CanvasRange => $"{Model.CanvasX:0} : {Model.CanvasY:0}";
    public string WindowCountText => _windowCount == 1 ? "1 window" : $"{_windowCount} windows";

    public int WindowCount
    {
        get => _windowCount;
        set
        {
            if (SetProperty(ref _windowCount, value))
            {
                OnPropertyChanged(nameof(WindowCountText));
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string TargetDisplayLabel
    {
        get => _targetDisplayLabel;
        private set => SetProperty(ref _targetDisplayLabel, value);
    }

    public void SetTargetDisplayLabel(string label) => TargetDisplayLabel = label;
}

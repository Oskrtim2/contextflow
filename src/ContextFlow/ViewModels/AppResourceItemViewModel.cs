using System.Windows.Media;
using ContextFlow.Infrastructure;
using ContextFlow.Models;
using ContextFlow.Services;

namespace ContextFlow.ViewModels;

/// <summary>One application family row in the Performance tab.</summary>
public sealed class AppResourceItemViewModel : ObservableObject
{
    public const double BarWidth = 96;

    private AppFamily _family;
    private AppOptimizationState _state;
    private ImageSource? _icon;
    private double _cpuBarWidth;
    private double _memoryBarWidth;

    public AppResourceItemViewModel(AppFamily family)
    {
        _family = family;
    }

    public int RootProcessId => _family.RootProcessId;
    public AppFamily Family => _family;
    public string Name => _family.Name;
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    public string ProcessCountText => _family.ProcessIds.Count == 1
        ? LocalizationService.Get("ProcessCountOne")
        : LocalizationService.Format("ProcessCountFormat", _family.ProcessIds.Count);

    public string CpuText => $"{_family.CpuPercent:0.0}%";
    public string MemoryText => FormatMemory(_family.MemoryMb);

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _icon is not null;

    public double CpuBarWidth
    {
        get => _cpuBarWidth;
        private set => SetProperty(ref _cpuBarWidth, value);
    }

    public double MemoryBarWidth
    {
        get => _memoryBarWidth;
        private set => SetProperty(ref _memoryBarWidth, value);
    }

    public AppOptimizationState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsOptimized));
                OnPropertyChanged(nameof(IsProtected));
                OnPropertyChanged(nameof(IsPlayingAudio));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(HasState));
                OnPropertyChanged(nameof(CanToggleOptimization));
            }
        }
    }

    public bool IsOptimized => State == AppOptimizationState.Optimized;
    public bool IsProtected => State == AppOptimizationState.Protected;
    public bool IsPlayingAudio => State == AppOptimizationState.PlayingAudio;
    public bool IsActive => State == AppOptimizationState.Active;
    public bool HasState => State != AppOptimizationState.None;

    /// <summary>Apps in use or protected cannot be optimized from the list.</summary>
    public bool CanToggleOptimization => State is not (AppOptimizationState.Active or AppOptimizationState.Protected);

    public bool IsUserExcluded { get; private set; }

    public string ProtectButtonText => LocalizationService.Get(IsUserExcluded ? "Unprotect" : "Protect");
    public string OptimizeButtonText => LocalizationService.Get(IsOptimized ? "Restore" : "Optimize");

    public string StateText => State switch
    {
        AppOptimizationState.Optimized => LocalizationService.Get("AppStateOptimized"),
        AppOptimizationState.Protected => LocalizationService.Get("AppStateProtected"),
        AppOptimizationState.PlayingAudio => LocalizationService.Get("AppStateAudio"),
        AppOptimizationState.Active => LocalizationService.Get("AppStateActive"),
        _ => string.Empty
    };

    public void Update(AppFamily family, AppOptimizationState state, bool userExcluded, double maxCpu, double maxMemoryMb)
    {
        _family = family;
        State = state;
        IsUserExcluded = userExcluded;
        CpuBarWidth = Math.Max(2, BarWidth * Math.Clamp(family.CpuPercent / Math.Max(maxCpu, 5), 0, 1));
        MemoryBarWidth = Math.Max(2, BarWidth * Math.Clamp(family.MemoryMb / Math.Max(maxMemoryMb, 1), 0, 1));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(ProcessCountText));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(ProtectButtonText));
        OnPropertyChanged(nameof(OptimizeButtonText));
        OnPropertyChanged(nameof(StateText));
    }

    public static string FormatMemory(double mb) =>
        mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
}

/// <summary>One AI insight card ("Brave uses 2.1 GB across 18 processes").</summary>
public sealed record InsightItemViewModel(string Glyph, string Title, string Detail, string Tone)
{
    public bool IsWarning => Tone == "warning";
    public bool IsSuccess => Tone == "success";
    public bool IsAccent => Tone == "accent";
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ContextFlow.Services;
using ContextFlow.ViewModels;

namespace ContextFlow.Views;

public partial class HudWindow : Window
{
    private bool _allowClose;
    private bool _isDarkTheme;

    public HudWindow(bool isDarkTheme)
    {
        _isDarkTheme = isDarkTheme;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.ApplyHudBackdrop(this, _isDarkTheme);
        StateChanged += (_, _) => UpdateWindowChromeState();
        Closing += OnClosing;
        UpdateWindowChromeState();
    }

    public void RefreshComposition(bool isDarkTheme)
    {
        _isDarkTheme = isDarkTheme;
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            WindowEffects.ApplyHudBackdrop(this, isDarkTheme);
        }
    }

    public void PrepareForOpen()
    {
        Opacity = 1;
        Root.BeginAnimation(OpacityProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        // The base opacity stays at 1. The entrance fade only lowers the
        // rendered opacity briefly; if the animation clock is throttled by DWM
        // (for example while a full-screen game owns the foreground), the HUD
        // remains visible instead of becoming permanently transparent.
        Root.Opacity = 1;
        RootScale.ScaleX = 0.955;
        RootScale.ScaleY = 0.955;
    }

    public void PlayOpenAnimation()
    {
        var fade = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        var scale = new DoubleAnimation(0.955, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        Root.Opacity = 1;
        Root.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    public async Task PlayHideAnimationAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        var scale = new DoubleAnimation(1, 0.965, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => completion.TrySetResult(true);
        Root.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        // Guarantee that a throttled/inactive timeline can never trap the user
        // inside an invisible HUD window.
        await Task.WhenAny(completion.Task, Task.Delay(260));
    }

    public void AllowClose() => _allowClose = true;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (DataContext is MainViewModel vm)
        {
            vm.Commands.Collapse.Execute(null);
        }
    }

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.IsCapturingHotkey)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            viewModel.CancelHotkeyCapture();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var windows = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        viewModel.TryCaptureHotkey(key, Keyboard.Modifiers, windows);
        e.Handled = true;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateWindowChromeState()
    {
        if (MaximizeButton is null || Shell is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.SetResourceReference(
            FrameworkElement.ToolTipProperty,
            maximized ? "TooltipRestore" : "TooltipMaximize");
        Shell.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(20);
    }

    private void ProcessList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is not null)
        {
            return;
        }

        if (ProcessList.SelectedItem is WindowItemViewModel window && DataContext is MainViewModel vm)
        {
            vm.Commands.FocusWindow.Execute(window);
            e.Handled = true;
        }
    }

    private void CapsulePill_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement placementTarget ||
            placementTarget.DataContext is not WindowItemViewModel window ||
            DataContext is not MainViewModel vm)
        {
            return;
        }

        // The process list refreshes every second. If the ContextMenu follows the
        // pill button as its PlacementTarget, WPF recalculates the popup position
        // when that row is updated and the menu visibly jumps to another place.
        // Anchor the popup to an absolute screen point captured at click time.
        var dpi = VisualTreeHelper.GetDpi(placementTarget);
        var screenPoint = placementTarget.PointToScreen(
            new Point(placementTarget.ActualWidth / 2, placementTarget.ActualHeight));

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("CapsuleContextMenuStyle"),
            PlacementTarget = this,
            Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = screenPoint.X / dpi.DpiScaleX,
            VerticalOffset = screenPoint.Y / dpi.DpiScaleY
        };

        menu.Items.Add(new MenuItem
        {
            Header = LocalizationService.Get("MoveWindowTo"),
            IsEnabled = false,
            Style = (Style)FindResource("CapsuleMenuItemStyle")
        });

        foreach (var capsule in vm.Capsules)
        {
            var item = new MenuItem
            {
                Header = capsule.Name,
                IsCheckable = true,
                IsChecked = string.Equals(capsule.Id, window.CapsuleId, StringComparison.Ordinal),
                Style = (Style)FindResource("CapsuleMenuItemStyle")
            };
            item.Click += (_, _) => vm.MoveWindowToCapsule(window, capsule);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

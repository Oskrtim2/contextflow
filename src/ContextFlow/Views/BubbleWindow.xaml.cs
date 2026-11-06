using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ContextFlow.Interop;
using ContextFlow.Services;

namespace ContextFlow.Views;

public sealed class BubbleMovedEventArgs : EventArgs
{
    public BubbleMovedEventArgs(double left, double top)
    {
        Left = left;
        Top = top;
    }

    public double Left { get; }
    public double Top { get; }
}

public partial class BubbleWindow : Window
{
    private const double EdgeMargin = 14;
    private Point _pressCursor;
    private Point _startWindow;
    private bool _isMouseDown;
    private bool _isDragging;
    private double _dpiScale = 1.0;

    public BubbleWindow()
    {
        InitializeComponent();
        Title = SingleInstanceService.BubbleWindowTitle;
        SourceInitialized += (_, _) =>
        {
            WindowEffects.ApplyBubbleWindow(this);
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        };
        MouseEnter += (_, _) => AnimateHover(1.06);
        MouseLeave += (_, _) => AnimateHover(1.0);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler<BubbleMovedEventArgs>? Moved;

    public void PositionOnScreen(AppSettingsPosition stored)
    {
        // SystemParameters.WorkArea is the primary monitor only. Use the full
        // virtual desktop for validation so a bubble saved on a secondary
        // display is not forced back to the primary monitor.
        var primary = SystemParameters.WorkArea;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var defaultLeft = primary.Right - Width - EdgeMargin;
        var defaultTop = primary.Top + primary.Height * 0.56;
        Left = stored.HasPosition ? stored.Left : defaultLeft;
        Top = stored.HasPosition ? stored.Top : defaultTop;

        if (Left < virtualLeft || Left > virtualRight - Width ||
            Top < virtualTop || Top > virtualBottom - Height)
        {
            Left = defaultLeft;
            Top = defaultTop;
        }
    }

    public void SnapIntoWorkArea()
    {
        // Clamp against the virtual desktop, not the primary monitor. The
        // previous implementation moved every dropped bubble back to the
        // primary display because WorkArea only describes that monitor.
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var left = Math.Clamp(Left, virtualLeft + 4, virtualRight - Width - 4);
        var top = Math.Clamp(Top, virtualTop + 4, virtualBottom - Height - 4);

        var leftDistance = Math.Abs(left - (virtualLeft + EdgeMargin));
        var rightDistance = Math.Abs(left - (virtualRight - Width - EdgeMargin));
        var topDistance = Math.Abs(top - (virtualTop + EdgeMargin));
        var bottomDistance = Math.Abs(top - (virtualBottom - Height - EdgeMargin));

        if (leftDistance < 24)
        {
            left = virtualLeft + EdgeMargin;
        }
        else if (rightDistance < 24)
        {
            left = virtualRight - Width - EdgeMargin;
        }

        if (topDistance < 24)
        {
            top = virtualTop + EdgeMargin;
        }
        else if (bottomDistance < 24)
        {
            top = virtualBottom - Height - EdgeMargin;
        }

        Left = left;
        Top = top;
    }

    public void PlayShowAnimation()
    {
        EnsureOnTop();

        // Keep Window.Opacity at 1. Animating a layered WPF window's opacity
        // across Hide/Show cycles can leave the native layered surface fully
        // transparent on some DWM configurations. The visual root gives the same
        // morph without touching the native window surface, and its base opacity
        // remains 1 so a throttled timeline cannot hide the utility completely.
        Opacity = 1;
        Root.BeginAnimation(OpacityProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Root.Opacity = 1;
        RootScale.ScaleX = 0.78;
        RootScale.ScaleY = 0.78;

        var fade = new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        var scale = new DoubleAnimation(0.78, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 },
            FillBehavior = FillBehavior.HoldEnd
        };
        Root.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    public async Task PlayHideAnimationAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        var scale = new DoubleAnimation(1, 0.72, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => completion.TrySetResult(true);
        Root.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        await Task.WhenAny(completion.Task, Task.Delay(220));
    }

    public void EnsureOnTop()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        Topmost = true;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private void AnimateHover(double target)
    {
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        _pressCursor = new Point(cursor.X, cursor.Y);
        _startWindow = new Point(Left, Top);
        _isMouseDown = true;
        _isDragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isMouseDown || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var deltaX = cursor.X - _pressCursor.X;
        var deltaY = cursor.Y - _pressCursor.Y;
        if (!_isDragging && Math.Abs(deltaX) + Math.Abs(deltaY) < 4)
        {
            return;
        }

        _isDragging = true;
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = _startWindow.X + deltaX / _dpiScale;
        Top = _startWindow.Y + deltaY / _dpiScale;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isMouseDown)
        {
            return;
        }

        _isMouseDown = false;
        ReleaseMouseCapture();
        if (_isDragging)
        {
            _isDragging = false;
            SnapIntoWorkArea();
            Moved?.Invoke(this, new BubbleMovedEventArgs(Left, Top));
        }
        else
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }
}

/// <summary>Small value-object used to restore a saved bubble position.</summary>
public readonly record struct AppSettingsPosition(bool HasPosition, double Left, double Top);

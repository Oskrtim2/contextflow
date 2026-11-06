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
        var workArea = SystemParameters.WorkArea;
        var defaultLeft = workArea.Right - Width - EdgeMargin;
        var defaultTop = workArea.Top + workArea.Height * 0.56;
        Left = stored.HasPosition ? stored.Left : defaultLeft;
        Top = stored.HasPosition ? stored.Top : defaultTop;

        if (Left < workArea.Left || Left > workArea.Right - Width ||
            Top < workArea.Top || Top > workArea.Bottom - Height)
        {
            Left = defaultLeft;
            Top = defaultTop;
        }
    }

    public void SnapIntoWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        var left = Math.Clamp(Left, workArea.Left + 4, workArea.Right - Width - 4);
        var top = Math.Clamp(Top, workArea.Top + 4, workArea.Bottom - Height - 4);

        var leftDistance = Math.Abs(left - (workArea.Left + EdgeMargin));
        var rightDistance = Math.Abs(left - (workArea.Right - Width - EdgeMargin));
        var topDistance = Math.Abs(top - (workArea.Top + EdgeMargin));
        var bottomDistance = Math.Abs(top - (workArea.Bottom - Height - EdgeMargin));

        if (leftDistance < 24)
        {
            left = workArea.Left + EdgeMargin;
        }
        else if (rightDistance < 24)
        {
            left = workArea.Right - Width - EdgeMargin;
        }

        if (topDistance < 24)
        {
            top = workArea.Top + EdgeMargin;
        }
        else if (bottomDistance < 24)
        {
            top = workArea.Bottom - Height - EdgeMargin;
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

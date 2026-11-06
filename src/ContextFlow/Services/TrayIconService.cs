using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ContextFlow.Services;

/// <summary>Notification-area presence for a windowless, always-on-top utility.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _aiItem;
    private readonly Forms.ToolStripMenuItem _isolationItem;
    private readonly Forms.ToolStripMenuItem _themeItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayIconService(
        Action showHud,
        Action toggleAi,
        Action toggleIsolation,
        Action toggleTheme,
        Action exit)
    {
        _icon = TryLoadIcon();
        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false
        };

        _openItem = new Forms.ToolStripMenuItem(LocalizationService.Get("TrayOpen"))
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold)
        };
        _openItem.Click += (_, _) => Dispatch(showHud);

        _aiItem = new Forms.ToolStripMenuItem(LocalizationService.Get("TrayAi")) { Checked = true };
        _aiItem.Click += (_, _) => Dispatch(toggleAi);

        _isolationItem = new Forms.ToolStripMenuItem(LocalizationService.Get("TrayIsolation"));
        _isolationItem.Click += (_, _) => Dispatch(toggleIsolation);

        _themeItem = new Forms.ToolStripMenuItem(LocalizationService.Get("TrayTheme"));
        _themeItem.Click += (_, _) => Dispatch(toggleTheme);

        var separator = new Forms.ToolStripSeparator();
        _exitItem = new Forms.ToolStripMenuItem(LocalizationService.Get("TrayExit"));
        _exitItem.Click += (_, _) => Dispatch(exit);

        menu.Items.Add(_openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_aiItem);
        menu.Items.Add(_isolationItem);
        menu.Items.Add(_themeItem);
        menu.Items.Add(separator);
        menu.Items.Add(_exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "ContextFlow · Ctrl+E+F",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatch(showHud);
    }

    public void UpdateLanguage()
    {
        _openItem.Text = LocalizationService.Get("TrayOpen");
        _aiItem.Text = LocalizationService.Get("TrayAi");
        _isolationItem.Text = LocalizationService.Get("TrayIsolation");
        _themeItem.Text = LocalizationService.Get("TrayTheme");
        _exitItem.Text = LocalizationService.Get("TrayExit");
    }

    public void Update(bool aiEnabled, bool isolationEnabled, bool darkTheme)
    {
        _aiItem.Checked = aiEnabled;
        _isolationItem.Checked = isolationEnabled;
        _themeItem.Checked = darkTheme;
    }

    public void ShowReadyBalloon()
    {
        try
        {
            _notifyIcon.ShowBalloonTip(
                2800,
                LocalizationService.Get("TrayReadyTitle"),
                LocalizationService.Get("TrayReadyText"),
                Forms.ToolTipIcon.None);
        }
        catch
        {
            // Balloon tips are best effort and can be disabled by policy.
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private static Icon TryLoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // Fall back to the generic application icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}

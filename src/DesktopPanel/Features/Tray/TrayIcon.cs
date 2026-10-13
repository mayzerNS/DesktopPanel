using System.Reflection;
using DesktopPanel.Core.Localization;
using WinForms = System.Windows.Forms;

namespace DesktopPanel.Features.Tray;

/// <summary>Значок в трее: статус в подсказке, двойной щелчок — панель, меню — настройки, логи, выход.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? LogsRequested;
    public event Action? ExitRequested;
    public event Action? RestartRequested;

    public TrayIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Assets.icon.ico");
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.open"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(Loc.T("tray.settings"), null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(Loc.T("tray.logs"), null, (_, _) => LogsRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("tray.restart"), null, (_, _) => RestartRequested?.Invoke());
        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Icon = stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream),
            Text = Loc.T("app.name"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    /// <summary>Текст при наведении на значок (до 127 символов).</summary>
    public void SetStatus(string text)
    {
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>Всплывающее уведомление Windows.</summary>
    public void Notify(string title, string text, bool warning = false)
    {
        _icon.ShowBalloonTip(5000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

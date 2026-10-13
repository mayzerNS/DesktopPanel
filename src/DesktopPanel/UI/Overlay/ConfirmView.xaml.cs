using System.Windows;
using System.Windows.Controls;
using DesktopPanel.Core.Localization;

namespace DesktopPanel.UI.Overlay;

/// <summary>Страница «Точно?»: заголовок, пояснение, необязательный переключатель, Отмена / действие.</summary>
public partial class ConfirmView : UserControl
{
    private Action<bool>? _ok;
    private Action? _cancel;

    public ConfirmView()
    {
        InitializeComponent();
    }

    public void Setup(string title, string text, string okText, Action<bool> onOk, Action onCancel,
        (string Title, string Sub, bool Default)? option)
    {
        _ok = onOk;
        _cancel = onCancel;
        TitleBlock.Text = title;
        BodyText.Text = text;
        OkButton.Content = okText;
        OkButton.IsEnabled = CancelButton.IsEnabled = true;
        OptionRow.Visibility = option is null ? Visibility.Collapsed : Visibility.Visible;
        if (option is { } o)
        {
            OptionRow.Title = o.Title;
            OptionRow.Sub = o.Sub;
            OptionSwitch.IsChecked = o.Default;
        }
    }

    /// <summary>Действие началось — кнопки неактивны, на кнопке «…».</summary>
    public void SetWorking()
    {
        OkButton.IsEnabled = CancelButton.IsEnabled = false;
        OkButton.Content = Loc.T("confirm.working");
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => _ok?.Invoke(OptionSwitch.IsChecked == true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Invoke();

    /// <summary>Переход на страницу: окошко вопроса появляется тем же ходом, что строки других вкладок.</summary>
    public void Enter() => DesktopPanel.UI.Theme.Fx.Enter(new FrameworkElement[] { Card });
}

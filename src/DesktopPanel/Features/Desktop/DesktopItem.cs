using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DesktopPanel.Features.Desktop;

/// <summary>Один ярлык, файл или папка с рабочего стола.</summary>
public sealed class DesktopItem : INotifyPropertyChanged
{
    public DesktopItem(string path, string name, bool isFolder)
    {
        Path = path;
        Name = name;
        IsFolder = isFolder;
    }

    /// <summary>Полный путь к файлу на рабочем столе.</summary>
    public string Path { get; }

    /// <summary>Имя, как его показывает Проводник (без .lnk и т.п.).</summary>
    public string Name { get; }

    public bool IsFolder { get; }

    private ImageSource? _icon;

    /// <summary>Значок. Пока не загружен — null (плитка показывает пустое место).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

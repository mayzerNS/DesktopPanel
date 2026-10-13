namespace DesktopPanel.Core.Config;

/// <summary>
/// Все настройки программы. Хранятся в %APPDATA%\DesktopPanel\config.json.
/// Добавляя поле — дайте ему значение по умолчанию; меняя смысл старого поля — поднимите CurrentVersion
/// и допишите миграцию в ConfigStore.
/// </summary>
public sealed class AppConfig
{
    public const int CurrentVersion = 2;

    /// <summary>Версия формата файла — по ней ConfigStore понимает, какие миграции нужны.</summary>
    public int Version { get; set; } = CurrentVersion;

    // --- общие ---
    public string Language { get; set; } = "ru";
    public bool FirstRunDone { get; set; }
    public bool Autostart { get; set; } = true;

    /// <summary>Сочетание для открытия оверлея, например "Win+C".</summary>
    public string Hotkey { get; set; } = "Win+C";

    /// <summary>Забирать у Windows сочетания, которые она держит сама (Win+C), — хуком клавиатуры. Только по желанию.</summary>
    public bool HotkeyTakeover { get; set; }

    // --- вид ---
    /// <summary>Затемнение фона панели, 0..100: 0 — без затемнения, 100 — чёрная.</summary>
    public int Darkness { get; set; } = 40;

    /// <summary>Ширина клетки сетки значков рабочего стола (точки).</summary>
    public int DesktopTileSize { get; set; } = 108;

    /// <summary>Размер значка на рабочем столе панели (точки).</summary>
    public int DesktopIconSize { get; set; } = 64;

    /// <summary>Свой цвет акцента "#RRGGBB"; пусто — цвет акцента Windows.</summary>
    public string AccentColor { get; set; } = "";
    public bool MusicVisualizer { get; set; } = true; // визуализация музыки за стеклом панели
    public string AccentLastCustom { get; set; } = ""; // последний выбранный свой цвет — к нему возвращает тумблер «Системный»

    /// <summary>Всплывающие подсказки: "full", "minimal" (только сообщающие что-то), "off". Кружки «?» — всегда.</summary>
    public string TipsMode { get; set; } = "full";

    /// <summary>Ограничение кадров всех анимаций, 30–360 (0 — частота монитора). Действует после перезапуска.</summary>
    public int AnimFps { get; set; } // 0 — как у монитора
    public bool AnimationsOff { get; set; }
    public bool MiniCollapsed { get; set; } // уголок сворачивается к краю экрана (разворачивается при наведении) // «Выключить анимации»: всё меняется сразу

    /// <summary>Непрозрачность фона подсказок, 30..100 %.</summary>
    public int TipsOpacity { get; set; } = 94;

    // --- рабочий стол в панели ---
    /// <summary>Свой порядок плиток (пути ярлыков и "group:id"); пусто — по алфавиту, как в Проводнике.</summary>
    public List<string> IconOrder { get; set; } = new();

    /// <summary>Показывать корзину среди значков.</summary>
    public bool ShowRecycleBin { get; set; } = true;

    /// <summary>Открывать значки двойным щелчком (иначе — одинарным).</summary>
    public bool DesktopDoubleClick { get; set; }

    /// <summary>Поиск в панели (печатаешь — ищет).</summary>
    public bool SearchEnabled { get; set; } = true;

    /// <summary>Искать и в системе: программы из меню «Пуск» и файлы (индекс поиска Windows).</summary>
    public bool SearchSystem { get; set; } = true;

    // --- «в уголок» ---
    /// <summary>В уголке только значки, без подписей.</summary>
    public bool MiniIconsOnly { get; set; }

    /// <summary>Где стоит плашка (в пикселях экрана); null — в правом нижнем углу.</summary>
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }

    // --- игры ---
    /// <summary>Если панель не видна поверх игры (эксклюзивный полноэкранный режим) — свернуть игру.</summary>
    public bool GameMinimize { get; set; } = true;

    /// <summary>После закрытия панели развернуть игру обратно и вернуть ей фокус.</summary>
    public bool GameRestore { get; set; } = true;

    // --- сочетания Проводника ---
    /// <summary>Значение DisabledHotkeys до того, как программа его поменяла (null — не меняла).</summary>
    public string? ShellHotkeysOriginal { get; set; }

    // --- значки рабочего стола ---
    /// <summary>Свои имена значков: путь → имя (сами файлы не трогаем).</summary>
    public Dictionary<string, string> IconNames { get; set; } = new();

    /// <summary>Свои картинки значков: путь → файл .png в %APPDATA%\DesktopPanel\icons.</summary>
    public Dictionary<string, string> IconImages { get; set; } = new();

    /// <summary>Группы значков (как папки на iPhone).</summary>
    public List<IconGroup> IconGroups { get; set; } = new();

    // --- прокси для Telegram (tg-ws-proxy) ---
    /// <summary>«Подгружать прокси для Telegram»: скачан и управляется программой.</summary>
    public bool TgProxyEnabled { get; set; }

    /// <summary>Был включён — при запуске программы включится снова.</summary>
    public bool TgProxyOn { get; set; }

    public string TgProxyVersion { get; set; } = "";
    public int TgProxyPort { get; set; } = 1443;

    /// <summary>Секрет прокси (32 hex-символа); создаётся при первом запуске.</summary>
    public string TgProxySecret { get; set; } = "";

    public DateTime TgProxyChecked { get; set; }

    /// <summary>Адаптеры (GUID), на которых программа выключила IPv6; пусто — не выключала.</summary>
    public List<string> Ipv6DisabledOn { get; set; } = new();

    // --- мини-браузер ---
    public bool PipEnabled { get; set; } = true;
    public string PipUrl { get; set; } = "https://www.youtube.com";
    public int? PipX { get; set; }
    public int? PipY { get; set; }
    public int PipWidth { get; set; } = 480;
    public int PipHeight { get; set; } = 270;

    /// <summary>Соотношение сторон окна ("16:9", "4:3", "21:9"): окно тянется только с ним.</summary>
    public string PipAspect { get; set; } = "16:9";

    /// <summary>Кнопки боковой панели включают режим взаимодействия, щелчок мимо браузера — выключает.</summary>
    public bool PipAutoInteract { get; set; } = true;
    public bool PipClickThrough { get; set; } // вне режима взаимодействия щелчки проходят сквозь окно
    public bool PipDoubleClick { get; set; } = true; // двойной щелчок по браузеру переключает режимы по кругу

    /// <summary>Масштаб страницы «показать всё» (мелко, видно весь сайт) вместо обычного.</summary>
    public bool PipZoomFit { get; set; }

    /// <summary>"16:9" → 1.777…</summary>
    public static double AspectRatio(string aspect)
    {
        var p = aspect.Split(':');
        return p.Length == 2 && double.TryParse(p[0], out var w) && double.TryParse(p[1], out var h) && w > 0 && h > 0
            ? w / h : 16 / 9.0;
    }

    /// <summary>Непрозрачность окна, 20..100 %.</summary>
    public int PipOpacity { get; set; } = 100;

    /// <summary>Вне режима взаимодействия клики проходят сквозь окно.</summary>

    /// <summary>Ставить видео на паузу, когда окно скрыто.</summary>
    public bool PipAutoPause { get; set; } = true;

    /// <summary>Закрывать скрытый мини-браузер через минуту (освобождает ~400 МБ), открывается заново чуть дольше.</summary>
    public bool PipFreeMemory { get; set; } = true;

    public List<PipFavorite> PipFavorites { get; set; } = new()
    {
        new PipFavorite { Name = "YouTube", Url = "https://www.youtube.com" },
        new PipFavorite { Name = "Twitch", Url = "https://www.twitch.tv" },
    };

    /// <summary>Сочетания мини-браузера: действие → сочетание ("" — не назначено).</summary>
    public Dictionary<string, string> PipHotkeys { get; set; } = new();

    /// <summary>Сочетания действий программы (Zapret, HDR, вкладки…): действие → сочетание; нет записи — не назначено.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    // --- Zapret ---
    /// <summary>«Подгружать Zapret»: раздел Zapret включён, программа скачивает и обновляет его.</summary>
    public bool ZapretEnabled { get; set; }

    /// <summary>Zapret был включён — после перезапуска программы включится снова.</summary>
    public bool ZapretOn { get; set; }

    public string ZapretStrategy { get; set; } = "general.bat";

    /// <summary>Папка установленной версии (внутри %LOCALAPPDATA%\DesktopPanel\zapret).</summary>
    public string ZapretRoot { get; set; } = "";

    public string ZapretVersion { get; set; } = "";

    /// <summary>Когда последний раз проверяли обновления.</summary>
    public DateTime ZapretChecked { get; set; }

    public bool ZapretAutoUpdate { get; set; } = true;

    /// <summary>Результаты теста стратегий: имя .bat → итог.</summary>
    public Dictionary<string, Features.Zapret.TestResult> ZapretTests { get; set; } = new();

    /// <summary>Выбранные фейковые пакеты (слот → имя), переживают обновление Zapret.</summary>
    public Dictionary<string, string> ZapretFakes { get; set; } = new();
}

public sealed class IconGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Пути значков в группе (по порядку).</summary>
    public List<string> Items { get; set; } = new();
}

public sealed class PipFavorite
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

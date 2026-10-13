using System.Text.Json;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Core.CrashGuard;

/// <summary>
/// Журнал того, что программа поменяла в системе (HDR, скрытые значки, запущенный Zapret).
/// Пишется на диск при каждом изменении. Если программа упала, при следующем запуске журнал
/// остаётся «грязным» — и всё, что в нём записано, откатывается.
/// </summary>
public sealed class StateJournal
{
    public sealed class State
    {
        /// <summary>true, пока программа работает; false — вышла штатно.</summary>
        public bool Running { get; set; }

        /// <summary>Значки рабочего стола скрыты нами (вернуть видимыми).</summary>
        public bool IconsHiddenByUs { get; set; }

        /// <summary>HDR был до нас (null — мы HDR не трогали).</summary>
        public bool? HdrOriginal { get; set; }

        /// <summary>Zapret запущен нами (остановить).</summary>
        public bool ZapretStartedByUs { get; set; }
    }

    private static string FilePath => Path.Combine(Paths.AppData, "state.json");
    private readonly object _gate = new();

    public State Current { get; private set; } = new();

    /// <summary>Что осталось от прошлого запуска, если он завершился аварийно (иначе null).</summary>
    public State? LeftoverFromCrash { get; private set; }

    public void Start()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var prev = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath));
                if (prev is { Running: true })
                {
                    LeftoverFromCrash = prev;
                    Log.Warn("crashguard: прошлый запуск завершился аварийно — откатываю его изменения");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("crashguard: журнал не читается", ex);
        }
        Current = new State { Running = true };
        Save();
    }

    /// <summary>Изменить запись и сразу сохранить на диск.</summary>
    public void Update(Action<State> change)
    {
        lock (_gate)
        {
            change(Current);
            Save();
        }
    }

    /// <summary>Штатный выход: всё откатили — журнал чистый.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            Current = new State { Running = false };
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.AppData);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("crashguard: журнал не записать", ex);
        }
    }
}

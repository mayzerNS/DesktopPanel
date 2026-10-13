using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Core.Config;

/// <summary>
/// Загрузка и сохранение настроек. Файл пишется атомарно (сначала во временный, потом подменяется),
/// поэтому сбой во время записи не портит настройки. Испорченный файл откладывается в config.bad.json.
/// </summary>
public static class ConfigStore
{
    public static string FilePath => Path.Combine(Paths.AppData, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Миграции: ключ — версия, ИЗ которой переходим. Каждая правит JSON «на месте» и переводит его на версию +1.
    /// Пример на будущее: [1] = j => { j["NewName"] = j["OldName"]?.DeepClone(); j.Remove("OldName"); }
    /// </summary>
    private static readonly Dictionary<int, Action<JsonObject>> Migrations = new()
    {
        // 1 → 2: «Прозрачность» (100 — только стекло) стала «Затемнением» (0 — без затемнения)
        [1] = j =>
        {
            var t = 60.0;
            if (j["Transparency"] is JsonValue tv && !tv.TryGetValue(out t)) t = 60;
            string? mode = null;
            if (j["GlassMode"] is JsonValue gv) gv.TryGetValue(out mode);
            j["Darkness"] = Math.Clamp((int)Math.Round((100 - t) * 0.9), 0, 100);
            j.Remove("Transparency");
            j.Remove("GlassMode");
        },
    };

    public static AppConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            Log.Info("config: файла нет — первый запуск");
            return new AppConfig();
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(FilePath),
                documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                as JsonObject ?? throw new InvalidDataException("в файле не объект");

            var version = node["Version"]?.GetValue<int>() ?? 0;
            if (version > AppConfig.CurrentVersion)
                Log.Warn($"config: версия {version} новее программы ({AppConfig.CurrentVersion}) — читаю что понимаю");

            while (version < AppConfig.CurrentVersion)
            {
                if (Migrations.TryGetValue(version, out var migrate))
                    migrate(node);
                version++;
                node["Version"] = version;
                Log.Info($"config: перевёл на версию {version}");
            }

            var cfg = node.Deserialize<AppConfig>(Options) ?? new AppConfig();
            Normalize(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error("config: не читается, начинаю с настроек по умолчанию", ex);
            try { File.Copy(FilePath, Path.Combine(Paths.AppData, "config.bad.json"), overwrite: true); } catch { }
            return new AppConfig();
        }
    }

    private static readonly object SaveGate = new();

    /// <summary>Сохранить. Можно звать из любого потока (Zapret сохраняет из фона).</summary>
    public static void Save(AppConfig cfg)
    {
        lock (SaveGate)
        try
        {
            Directory.CreateDirectory(Paths.AppData);
            cfg.Version = AppConfig.CurrentVersion;
            var tmp = FilePath + ".tmp";
            string json;
            try { json = JsonSerializer.Serialize(cfg, Options); }
            catch (InvalidOperationException)
            {
                // словарь поменяли из фона прямо во время записи — пробуем ещё раз
                Thread.Sleep(20);
                json = JsonSerializer.Serialize(cfg, Options);
            }
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("config: не сохранить", ex);
        }
    }

    /// <summary>Подрезаем значения, которые могли испортить руками.</summary>
    private static void Normalize(AppConfig c)
    {
        c.Darkness = Math.Clamp(c.Darkness, 0, 100);
        c.TipsOpacity = Math.Clamp(c.TipsOpacity, 0, 100);
        c.DesktopIconSize = Math.Clamp(c.DesktopIconSize, 32, 136);
        c.DesktopTileSize = Math.Clamp(c.DesktopTileSize, 72, 200);
        if (c.TipsMode is not ("full" or "minimal" or "off")) c.TipsMode = "full";
        if (c.AnimFps != 0) c.AnimFps = Math.Clamp(c.AnimFps, 30, 360);
        if (c.TgProxyPort is < 1024 or > 65535) c.TgProxyPort = 1443;
        c.TgProxySecret ??= "";
        c.Ipv6DisabledOn ??= new();
        c.TgProxyVersion ??= "";
        c.AccentColor ??= "";
        c.AccentLastCustom ??= "";
        c.IconOrder ??= new();
        if (string.IsNullOrWhiteSpace(c.Language)) c.Language = "ru";
        if (string.IsNullOrWhiteSpace(c.Hotkey)) c.Hotkey = "Win+C";
        if (!Hotkeys.Hotkey.Parse(c.Hotkey).IsValid) c.Hotkey = "Win+C"; // одиночную клавишу назначать нельзя
        c.PipOpacity = Math.Clamp(c.PipOpacity, 0, 100);
        c.PipWidth = Math.Clamp(c.PipWidth, 200, 4000);
        if (c.PipAspect is not ("16:9" or "4:3" or "21:9")) c.PipAspect = "16:9";
        c.PipHeight = (int)Math.Round(c.PipWidth / AppConfig.AspectRatio(c.PipAspect)); // высота всегда по соотношению
        c.PipUrl ??= "https://www.youtube.com";
        c.PipFavorites ??= new();
        c.PipHotkeys ??= new();
        c.Shortcuts ??= new();
        // кнопки мыши без модификатора больше не назначаются (их обычное действие не подавить) — такие убираем
        foreach (var map in new[] { c.PipHotkeys, c.Shortcuts })
            foreach (var k in map.Where(p => Hotkeys.Hotkey.Parse(p.Value) is { IsMouse: true, IsValid: false }).Select(p => p.Key).ToList())
                map.Remove(k);
        c.IconNames ??= new();
        c.IconImages ??= new();
        c.IconGroups ??= new();
        c.IconGroups.RemoveAll(g => g is null || g.Items is null || g.Items.Count == 0);
        c.ZapretTests ??= new();
        c.ZapretFakes ??= new();
        c.ZapretStrategy ??= "general.bat";
        c.ZapretRoot ??= "";
        c.ZapretVersion ??= "";
    }
}

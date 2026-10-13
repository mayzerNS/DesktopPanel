using System.Text.Json.Nodes;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Pip;

/// <summary>
/// «Безопасный DNS» (DNS через HTTPS) для мини-браузера. Обычные DNS-запросы провайдер может подменять
/// (www.youtube.com → «домен не существует»), и даже с VPN они часто идут мимо туннеля. Зашифрованные запросы
/// к Cloudflare/Google так не подменить. Настройку движок Edge хранит в файле «Local State» папки браузера —
/// дописываем её туда до запуска WebView2 (остальные настройки в файле не трогаем).
/// </summary>
public static class SecureDns
{
    /// <summary>Два сервера: если один недоступен, браузер спросит второй.</summary>
    private const string Templates = "https://cloudflare-dns.com/dns-query https://dns.google/dns-query";

    public static void Enable(string userDataDir)
    {
        try
        {
            var dir = Path.Combine(userDataDir, "EBWebView");
            var file = Path.Combine(dir, "Local State");
            Directory.CreateDirectory(dir);
            var root = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject()
                                         : new JsonObject();
            if (root["dns_over_https"] is JsonObject cur
                && (string?)cur["mode"] == "secure" && (string?)cur["templates"] == Templates) return;
            root["dns_over_https"] = new JsonObject { ["mode"] = "secure", ["templates"] = Templates };
            File.WriteAllText(file, root.ToJsonString());
            Log.Info("pip: включён безопасный DNS");
        }
        catch (Exception ex)
        {
            Log.Warn($"pip: безопасный DNS не включить ({ex.Message})");
        }
    }
}

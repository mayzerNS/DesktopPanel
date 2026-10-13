using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopPanel.Core;
using DesktopPanel.Core.Localization;
using DesktopPanel.Core.Logging;

namespace DesktopPanel.Features.Zapret;

/// <summary>Скачивание и обновление Zapret с GitHub (релизы Flowseal/zapret-discord-youtube).</summary>
public static class ZapretUpdater
{
    /// <summary>Общий HttpClient для всего, что Zapret берёт из сети.</summary>
    public static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Paths.AppName, "1.0"));
        h.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return h;
    }

    /// <summary>Последняя версия: тег и ссылка на zip.</summary>
    public static async Task<(string Tag, string Url)> LatestAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ZapretPaths.ReleaseApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new ZapretException(Loc.T("zapret.err.no_zip"));

        string? url = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        if (url is null && root.TryGetProperty("zipball_url", out var zb)) url = zb.GetString();
        if (string.IsNullOrEmpty(url)) throw new ZapretException(Loc.T("zapret.err.no_zip"));
        return (tag, url);
    }

    /// <summary>Скачать и распаковать версию. Возвращает папку, в которой лежит bin\winws.exe.</summary>
    public static async Task<string> DownloadAsync(string tag, string url, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ZapretPaths.Dir);
        var dest = Path.Combine(ZapretPaths.Dir, Regex.Replace(tag, @"[^\w.\-]", "_"));
        var tmp = Path.Combine(ZapretPaths.Dir, "_download.zip");
        try
        {
            await using (var src = await Http.GetStreamAsync(url, ct))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);

            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            ZipFile.ExtractToDirectory(tmp, dest, overwriteFiles: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }

        var found = Directory.EnumerateDirectories(dest, "*", SearchOption.AllDirectories).Prepend(dest)
            .FirstOrDefault(d => File.Exists(ZapretPaths.Winws(d)));
        return found ?? throw new ZapretException(Loc.T("zapret.err.no_winws_in_zip"));
    }

    /// <summary>Свои списки (…user…) и флаги (*.enabled) переезжают в новую версию.</summary>
    public static void CopyUserFiles(string oldRoot, string newRoot)
    {
        foreach (var sub in new[] { "lists", "utils" })
        {
            var src = Path.Combine(oldRoot, sub);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(newRoot, sub);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                var n = Path.GetFileName(f);
                var low = n.ToLowerInvariant();
                if (!low.Contains("user") && !low.EndsWith(".enabled")) continue;
                try { File.Copy(f, Path.Combine(dst, n), overwrite: true); }
                catch (Exception ex) { Log.Warn($"zapret: не перенести {n} ({ex.Message})"); }
            }
        }
    }

    /// <summary>Удалить старые версии, оставив ту, где лежит keep.</summary>
    public static void Cleanup(string keep)
    {
        if (!Directory.Exists(ZapretPaths.Dir)) return;
        var top = "";
        if (!string.IsNullOrEmpty(keep))
            top = Path.GetRelativePath(ZapretPaths.Dir, keep).Split(Path.DirectorySeparatorChar)[0];
        foreach (var d in Directory.GetDirectories(ZapretPaths.Dir))
        {
            if (string.Equals(Path.GetFileName(d), top, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(d, true); }
            catch (Exception ex) { Log.Warn($"zapret: не удалить старую версию {d} ({ex.Message})"); }
        }
    }

    /// <summary>Полностью убрать файлы Zapret (драйвер отпускает их не сразу — несколько попыток).</summary>
    public static void DeleteAll()
    {
        for (var i = 0; i < 20 && Directory.Exists(ZapretPaths.Dir); i++)
        {
            try { Directory.Delete(ZapretPaths.Dir, true); }
            catch { Thread.Sleep(250); }
        }
        try
        {
            var parent = Path.GetDirectoryName(ZapretPaths.Dir)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch { }
    }
}

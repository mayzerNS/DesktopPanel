using System.Net;
using System.Text;
using DesktopPanel.Core;
using DesktopPanel.Core.Config;
using DesktopPanel.Core.Localization;

namespace DesktopPanel.Features.Pip;

/// <summary>
/// Стартовая страница мини-браузера: поиск (Google) и плитки избранного. Это обычный файл в папке браузера,
/// WebView2 отдаёт его по адресу https://start.desktoppanel/ (SetVirtualHostNameToFolderMapping).
/// </summary>
public static class StartPage
{
    public const string Host = "start.desktoppanel";
    public const string Url = "https://" + Host + "/index.html";

    public static string Folder => Path.Combine(Paths.LocalData, "StartPage"); // вне папки данных WebView2: оттуда браузер отдавал ERR_ACCESS_DENIED

    public static bool IsStart(string? url) =>
        url is not null && url.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Записать страницу заново (при запуске и когда поменялось избранное).</summary>
    public static void Write(AppConfig cfg)
    {
        Directory.CreateDirectory(Folder);
        var tiles = new StringBuilder();
        foreach (var f in cfg.PipFavorites)
        {
            var host = Uri.TryCreate(f.Url, UriKind.Absolute, out var u) ? u.Host : "";
            tiles.Append($"""
                <a class="tile" href="{Enc(f.Url)}">
                  <img src="https://www.google.com/s2/favicons?domain={Enc(host)}&sz=64" alt="">
                  <span>{Enc(f.Name)}</span>
                </a>
                """);
        }
        var html = $$"""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{Enc(Loc.T("pip.start.title"))}}</title>
            <style>
              :root { color-scheme: dark; }
              html, body { margin: 0; height: 100%; background: #101014; color: #eee;
                           font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif; }
              body { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4vh; }
              form { width: min(86vw, 640px); }
              input { width: 100%; box-sizing: border-box; padding: 0.8em 1.2em; font-size: clamp(14px, 3.4vw, 20px);
                      border-radius: 999px; border: 1px solid #ffffff30; background: #ffffff12; color: #fff; outline: none; }
              input:focus { border-color: #ffffff70; background: #ffffff1c; }
              .tiles { display: flex; flex-wrap: wrap; justify-content: center; gap: 2vw; width: min(90vw, 720px); }
              .tile { display: flex; flex-direction: column; align-items: center; gap: 6px; width: clamp(64px, 14vw, 96px);
                      padding: 10px 4px; border-radius: 14px; color: #ddd; text-decoration: none; font-size: clamp(11px, 2.4vw, 13px); }
              .tile:hover { background: #ffffff14; }
              .tile img { width: clamp(28px, 7vw, 40px); height: clamp(28px, 7vw, 40px); border-radius: 10px; background: #ffffff10; }
              .tile span { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            </style></head>
            <body>
              <form id="f" autocomplete="off">
                <input id="q" placeholder="{{Enc(Loc.T("pip.start.search"))}}">
              </form>
              <div class="tiles">{{tiles}}</div>
              <script>
                document.getElementById('f').addEventListener('submit', e => {
                  e.preventDefault();
                  const t = document.getElementById('q').value.trim();
                  if (!t) return;
                  const looksLikeUrl = /^[a-z]+:\/\//i.test(t) || (!/\s/.test(t) && /\.[a-z]{2,}(\/|$)/i.test(t));
                  location.href = looksLikeUrl ? (/^[a-z]+:\/\//i.test(t) ? t : 'https://' + t)
                                               : 'https://www.google.com/search?q=' + encodeURIComponent(t);
                });
              </script>
            </body></html>
            """;
        File.WriteAllText(Path.Combine(Folder, "index.html"), html, new UTF8Encoding(false));
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}

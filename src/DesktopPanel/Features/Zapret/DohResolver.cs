using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DesktopPanel.Features.Zapret;

/// <summary>
/// Адреса сайтов через DNS-over-HTTPS (Cloudflare, запасной — Google). Обычный DNS провайдер может подменять
/// (www.youtube.com → «хост неизвестен»), и тогда тест стратегий видел бы проблему DNS, а не блокировку.
/// </summary>
public static class DohResolver
{
    private static readonly string[] Servers =
    {
        "https://cloudflare-dns.com/dns-query?name={0}&type=A",
        "https://dns.google/resolve?name={0}&type=A",
    };

    /// <summary>IPv4-адреса хоста или пустой массив (тогда пусть решает обычный DNS).</summary>
    public static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        foreach (var server in Servers)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(server, Uri.EscapeDataString(host)));
                req.Headers.Accept.ParseAdd("application/dns-json");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var resp = await ZapretUpdater.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("Answer", out var answers)) continue;
                var list = new List<IPAddress>();
                foreach (var a in answers.EnumerateArray())
                    if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1 // A-запись (CNAME пропускаем)
                        && IPAddress.TryParse(a.GetProperty("data").GetString(), out var ip))
                        list.Add(ip);
                if (list.Count > 0) return list.ToArray();
            }
            catch
            {
                // следующий сервер
            }
        }
        return Array.Empty<IPAddress>();
    }
}

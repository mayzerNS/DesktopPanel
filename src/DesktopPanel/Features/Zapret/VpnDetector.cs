using System.Net.NetworkInformation;

namespace DesktopPanel.Features.Zapret;

/// <summary>
/// Работает ли VPN в режиме туннеля (виртуальный сетевой адаптер: Wintun, TAP, WireGuard, sing-box и т.п.).
/// Тогда трафик и так идёт через VPN — Zapret не нужен, а вмешиваясь в пакеты туннеля, может всё сломать.
/// </summary>
public static class VpnDetector
{
    private static readonly string[] Markers =
        { "tun", "tap-", "tap ", "wintun", "wireguard", "vpn", "sing-box", "openvpn", "outline", "amnezia" };

    private static (DateTime At, string? Name) _cache;

    /// <summary>Имя адаптера VPN или null. Результат кэшируется на несколько секунд (интерфейс спрашивает часто).</summary>
    public static string? ActiveTunnel()
    {
        if (DateTime.Now - _cache.At < TimeSpan.FromSeconds(5)) return _cache.Name;
        string? found = null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (text.Contains("teredo") || text.Contains("isatap") || text.Contains("6to4")) continue; // туннели самой Windows
                if (Markers.Any(text.Contains))
                {
                    found = ni.Name;
                    break;
                }
            }
        }
        catch
        {
            // нет данных — молчим
        }
        _cache = (DateTime.Now, found);
        return found;
    }
}

namespace DesktopPanel.Features.Zapret;

/// <summary>Итог проверки одной стратегии (или «без обхода»).</summary>
public sealed class TestResult
{
    /// <summary>Сколько проверочных сайтов открылось.</summary>
    public int Ok { get; set; }

    public int Total { get; set; }

    /// <summary>Среднее время ответа открывшихся сайтов, мс.</summary>
    public int? Ms { get; set; }

    /// <summary>Какие сайты не открылись.</summary>
    public List<string> Failed { get; set; } = new();

    /// <summary>Стратегия не запустилась (текст причины) — тогда Ok = 0.</summary>
    public string? Error { get; set; }

    public DateTime When { get; set; } = DateTime.Now;

    public static TestResult From(IReadOnlyDictionary<string, int?> results)
    {
        var ok = results.Values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return new TestResult
        {
            Ok = ok.Count,
            Total = results.Count,
            Ms = ok.Count > 0 ? (int)Math.Round(ok.Average()) : null,
            Failed = results.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList(),
        };
    }

    public static TestResult Failure(string error, int total) => new() { Total = total, Error = error };
}

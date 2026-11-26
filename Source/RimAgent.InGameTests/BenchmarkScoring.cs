#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RimAgent.InGameTests;

/// <summary>Benchmark 判分與統計的純字串邏輯；單元測試以連結方式編進 RimAgent.Tests。</summary>
internal static partial class BenchmarkScoring
{
    /// <summary>回覆裡接在「天、日、day」前的數字；全形數字先轉半形。</summary>
    internal static IEnumerable<float> Days(string? reply) =>
        reply == null
            ? []
            : DaysPattern.Matches(reply.Normalize(NormalizationForm.FormKC))
                .Select(m => float.TryParse(m.Groups["days"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float days) ? days : (float?)null)
                .Where(d => d.HasValue)
                .Select(d => d!.Value);

    /// <summary>去掉空白、全形括號與逗號轉半形，座標才能寫成 (x,z) 比對。</summary>
    internal static string FlattenCoordinates(string? reply) =>
        (reply ?? string.Empty).Normalize(NormalizationForm.FormKC).Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>回覆寫出幾個不同座標；照抄指令裡的 <paramref name="ignored"/> 不算，除非它就是 <paramref name="answer"/>。</summary>
    internal static int DistinctCells(string flat, string answer, IReadOnlyCollection<string> ignored) =>
        CoordinatePattern.Matches(flat).Select(m => m.Value).Where(v => v == answer || !ignored.Contains(v)).Distinct(StringComparer.Ordinal).Count();

    /// <summary>成對差的平均與 95% 百分位信賴區間；固定種子，同一份結果每次算出一樣的區間。</summary>
    internal static (double Mean, double Low, double High) PairedBootstrap(IReadOnlyList<int> diffs, int samples = 10000)
    {
#pragma warning disable S2245 // 重抽樣只要可重現，不需要密碼學等級的亂數。
        var random = new Random(17);
#pragma warning restore S2245
        var means = new double[samples];
        for (int s = 0; s < samples; s++)
        {
            int sum = 0;
            for (int i = 0; i < diffs.Count; i++) sum += diffs[random.Next(diffs.Count)];
            means[s] = (double)sum / diffs.Count;
        }
        Array.Sort(means);
        return (diffs.Average(), means[(int)(samples * 0.025)], means[(int)(samples * 0.975) - 1]);
    }

    /// <summary>接在「天、日、day」前的數字；只要求有數字的話，回覆裡的殖民者人數也能過關。</summary>
    [GeneratedRegex(@"(?<days>[0-9]+(?:\.[0-9]+)?)\s*(?:天|日|days?\b)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DaysPattern { get; }

    [GeneratedRegex(@"\([0-9]+,[0-9]+\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CoordinatePattern { get; }
}

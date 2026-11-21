#nullable disable

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RimAgent.UI;

/// <summary>框架的 Markdig 管線沒有數學擴充、遊戲內也沒有 KaTeX，只能保守轉換成純文字；純 BCL 以便單元測試。</summary>
internal static class LatexMath
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex CodeRegion = new(
        @"(?:^|(?<=\n))(?<fence>`{3,}|~{3,})[\s\S]*?(?:\n\k<fence>[ \t]*(?=\r?\n|\z)|\z)|`+[^`\n]*`+",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        RegexTimeout);

    private static readonly Regex DisplayMath = new(
        @"\$\$(?<body>[\s\S]+?)\$\$",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>行內數學；後面不接數字，以排除 "$5-$10" 這類金額區間。</summary>
    private static readonly Regex InlineMath = new(
        @"(?<!\\)(?<!\$)\$(?!\$)(?!\s)(?<body>[^$\n]+?)(?<!\s)(?<!\\)\$(?!\$)(?!\d)",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex FracPattern = new(
        @"\\frac\s*\{(?<a>[^{}]*)\}\s*\{(?<b>[^{}]*)\}",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SqrtBracedPattern = new(
        @"\\sqrt\s*(?:\[[^{}]*\])?\s*\{(?<x>[^{}]*)\}",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SqrtSinglePattern = new(
        @"\\sqrt\s*(?<x>[A-Za-z0-9])",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex TextFamilyPattern = new(
        @"\\(?:text|mathrm|mathbf|mathit|textbf|textit|operatorname|boldsymbol)\s*\{(?<x>[^{}]*)\}",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SupBracedPattern = new(
        @"\^\{(?<x>[^{}]*)\}",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SupSinglePattern = new(
        @"\^(?<x>[0-9A-Za-z+\-=])",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SubBracedPattern = new(
        @"_\{(?<x>[^{}]*)\}",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SubSinglePattern = new(
        @"_(?<x>[0-9A-Za-z])",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SpacingPattern = new(
        @"\\(?:left|right)\s*|\\(?:quad|qquad)|\\[,;:\s!]",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex CommandPattern = new(
        @"\\(?<name>[A-Za-z]+)",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex EscapedCharPattern = new(
        @"\\(?<c>[\{\}%&_#])",
        RegexOptions.Compiled,
        RegexTimeout);

    private const string ScriptSrc = "0123456789+-=";
    private const string SupDst = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼";
    private const string SubDst = "₀₁₂₃₄₅₆₇₈₉₊₋₌";

    private struct MathRule
    {
        public readonly Regex Pattern;
        public readonly MatchEvaluator Replace;

        public MathRule(Regex pattern, MatchEvaluator replace)
        {
            Pattern = pattern;
            Replace = replace;
        }
    }

    private static readonly MatchEvaluator FracReplace =
        m => "(" + m.Groups["a"].Value + ")/(" + m.Groups["b"].Value + ")";
    private static readonly MatchEvaluator SqrtReplace = m => "√(" + m.Groups["x"].Value + ")";
    private static readonly MatchEvaluator SqrtSingleReplace = m => "√" + m.Groups["x"].Value;
    private static readonly MatchEvaluator PlainGroupReplace = m => m.Groups["x"].Value;
    private static readonly MatchEvaluator SupReplace = m => ConvertScript(m.Groups["x"].Value, superscript: true);
    private static readonly MatchEvaluator SubReplace = m => ConvertScript(m.Groups["x"].Value, superscript: false);
    private static readonly MatchEvaluator CommandReplace = m =>
    {
        string name = m.Groups["name"].Value;
        return CommandText.TryGetValue(name, out string plain) ? plain : name;
    };
    private static readonly MatchEvaluator EscapedReplace = m => m.Groups["c"].Value;

    private static readonly MathRule[] SegmentRules =
    [
        new(FracPattern, FracReplace),
        new(SqrtBracedPattern, SqrtReplace),
        new(SqrtSinglePattern, SqrtSingleReplace),
        new(TextFamilyPattern, PlainGroupReplace),
        new(SupBracedPattern, SupReplace),
        new(SupSinglePattern, SupReplace),
        new(SubBracedPattern, SubReplace),
        new(SubSinglePattern, SubReplace),
        new(SpacingPattern, _ => " "),
        new(CommandPattern, CommandReplace),
        new(EscapedCharPattern, EscapedReplace),
    ];

    /// <summary>只有遊戲內字體確定顯示得出的符號才進這張表；未知指令去反斜線留原名。</summary>
    private static readonly Dictionary<string, string> CommandText = new(StringComparer.Ordinal)
    {
        { "alpha", "α" }, { "beta", "β" }, { "gamma", "γ" }, { "delta", "δ" },
        { "epsilon", "ε" }, { "zeta", "ζ" }, { "eta", "η" }, { "theta", "θ" },
        { "lambda", "λ" }, { "mu", "μ" }, { "nu", "ν" }, { "pi", "π" },
        { "sigma", "σ" }, { "tau", "τ" }, { "phi", "φ" }, { "chi", "χ" },
        { "omega", "ω" }, { "Delta", "Δ" }, { "Sigma", "Σ" }, { "Omega", "Ω" },
        { "times", "×" }, { "cdot", "·" }, { "div", "÷" }, { "pm", "±" },
        { "leq", "≤" }, { "geq", "≥" }, { "neq", "≠" }, { "approx", "≈" },
        { "infty", "∞" }, { "sum", "Σ" }, { "prod", "Π" }, { "int", "∫" },
        { "partial", "∂" }, { "to", "→" }, { "rightarrow", "→" }, { "leftarrow", "←" },
        { "ldots", "…" }, { "cdots", "…" },
        { "log", "log" }, { "ln", "ln" }, { "lg", "lg" }, { "exp", "exp" },
        { "sin", "sin" }, { "cos", "cos" }, { "tan", "tan" },
        { "lim", "lim" }, { "min", "min" }, { "max", "max" },
    };

    public static string ConvertMath(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;
        if (markdown.IndexOf('$', StringComparison.Ordinal) < 0) return markdown;

        var sb = new StringBuilder(markdown.Length);
        int last = 0;
        foreach (Match region in CodeRegion.Matches(markdown))
        {
            sb.Append(ConvertMathInText(markdown.Substring(last, region.Index - last)));
            sb.Append(region.Value);
            last = region.Index + region.Length;
        }
        sb.Append(ConvertMathInText(markdown.Substring(last)));
        return sb.ToString();
    }

    private static string ConvertMathInText(string text)
    {
        if (text.IndexOf('$', StringComparison.Ordinal) < 0) return text;
        text = DisplayMath.Replace(text, m =>
        {
            string body = m.Groups["body"].Value;
            return string.IsNullOrWhiteSpace(body) ? m.Value : ConvertSegment(body, display: true);
        });
        text = InlineMath.Replace(text, m => ConvertSegment(m.Groups["body"].Value, display: false));
        return text;
    }

    private static string ConvertSegment(string body, bool display)
    {
        // \\ 是數學換行：顯示數學保留換行，行內數學壓成空白。
        string s = body.Replace("\\\\", display ? "\n" : " ", StringComparison.Ordinal);
        foreach (MathRule rule in SegmentRules) s = rule.Pattern.Replace(s, rule.Replace);
        s = s.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal).Replace('~', ' ');
        return s;
    }

    /// <summary>全是數字與 +-＝ 才轉 Unicode（遊戲字體一定有），否則保留 ^／_ 記號。</summary>
    private static string ConvertScript(string inner, bool superscript)
    {
        string dst = superscript ? SupDst : SubDst;
        if (inner.Length > 0)
        {
            var sb = new StringBuilder(inner.Length);
            foreach (char c in inner)
            {
                int i = ScriptSrc.IndexOf(c, StringComparison.Ordinal);
                if (i < 0) return (superscript ? "^" : "_") + inner;
                sb.Append(dst[i]);
            }
            return sb.ToString();
        }
        return inner;
    }
}

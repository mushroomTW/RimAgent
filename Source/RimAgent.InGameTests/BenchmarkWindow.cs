using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimAgent.Agent;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

internal enum BenchmarkState
{
    Pending,
    Running,
    Passed,
    Failed,
    /// <summary>模型服務出錯，不算進通過率。</summary>
    Invalid
}

internal sealed record BenchmarkScore(int Passed, int Total, int Tools, int Failures, float Seconds, long Input, long Output, string Model);

/// <summary>進度列表的一列；由 benchmark 更新，視窗只讀。</summary>
internal sealed class BenchmarkRow
{
    public string Id;
    public string Tier;
    public string Instruction;
    public BenchmarkState State;
    public float StartedAt;
    public float Seconds;
    public int Tools;
    public int Failures;
    public string Detail;
}

/// <summary>benchmark 的進度列表：不擋操作、可拖動，關掉後可從除錯動作再打開。</summary>
internal sealed class Window_BenchmarkProgress : Window
{
    private const float RowHeight = 26f;
    private const float HeaderHeight = 30f;

    private static readonly Color RunningColor = new(1f, 0.85f, 0.3f);
    private static readonly Color PassedColor = new(0.45f, 0.9f, 0.45f);
    private static readonly Color InvalidColor = new(0.75f, 0.6f, 0.9f);
    private static readonly Color FailedColor = new(1f, 0.45f, 0.4f);

    private readonly IReadOnlyList<BenchmarkRow> _rows;
    private Vector2 _scroll;

    public Window_BenchmarkProgress(IReadOnlyList<BenchmarkRow> rows)
    {
        _rows = rows;
        draggable = true;
        doCloseX = true;
        closeOnAccept = false;
        closeOnCancel = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        preventCameraMotion = false;
        focusWhenOpened = false;
    }

    public override Vector2 InitialSize => new(640f, Math.Min(Verse.UI.screenHeight - 100f, 150f + _rows.Count * RowHeight));

    protected override void SetInitialSizeAndPosition()
    {
        base.SetInitialSizeAndPosition();
        // 靠左，不蓋住畫面中央的代理視窗。
        windowRect.x = 10f;
    }

    public static void Show(IReadOnlyList<BenchmarkRow> rows)
    {
        Find.WindowStack.TryRemove(typeof(Window_BenchmarkProgress), doCloseSound: false);
        Find.WindowStack.Add(new Window_BenchmarkProgress(rows));
    }

    public override void DoWindowContents(Rect inRect)
    {
        int done = _rows.Count(r => r.State is BenchmarkState.Passed or BenchmarkState.Failed or BenchmarkState.Invalid);
        int failed = _rows.Count(r => r.State == BenchmarkState.Failed);
        int passed = _rows.Count(r => r.State == BenchmarkState.Passed);

        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 30f, 34f), "RimAgent.Benchmark.Title".Translate() + "  " + "RimAgent.Reasoning.Status".Translate(BenchmarkComponent.ReasoningLabel(BenchmarkComponent.Reasoning)));
        Text.Font = GameFont.Small;

        Rect bar = new(inRect.x, inRect.y + 38f, inRect.width, 24f);
        Widgets.FillableBar(bar, _rows.Count == 0 ? 0f : (float)done / _rows.Count);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(bar, "RimAgent.Benchmark.Progress".Translate(done, _rows.Count, passed, failed, done - passed - failed));
        Text.Anchor = TextAnchor.UpperLeft;

        Rect header = new(inRect.x, bar.yMax + 6f, inRect.width - 16f, HeaderHeight);
        DrawColumns(header, "RimAgent.Benchmark.Column.Task".Translate(), "RimAgent.Benchmark.Column.Status".Translate(),
            "RimAgent.Benchmark.Column.Tools".Translate(), "RimAgent.Benchmark.Column.Time".Translate(), Color.white);

        Rect outRect = new(inRect.x, header.yMax, inRect.width, inRect.yMax - header.yMax);
        Rect view = new(0f, 0f, inRect.width - 16f, _rows.Count * RowHeight);
        Widgets.BeginScrollView(outRect, ref _scroll, view);
        for (int i = 0; i < _rows.Count; i++) DrawRow(new Rect(0f, i * RowHeight, view.width, RowHeight), _rows[i], i);
        Widgets.EndScrollView();
    }

    private static void DrawRow(Rect rect, BenchmarkRow row, int index)
    {
        if (index % 2 == 1) Widgets.DrawLightHighlight(rect);
        Widgets.DrawHighlightIfMouseover(rect);
        Color color = row.State switch
        {
            BenchmarkState.Running => RunningColor,
            BenchmarkState.Passed => PassedColor,
            BenchmarkState.Failed => FailedColor,
            BenchmarkState.Invalid => InvalidColor,
            _ => Color.gray
        };
        bool started = row.State != BenchmarkState.Pending;
        float seconds = row.State == BenchmarkState.Running ? Time.realtimeSinceStartup - row.StartedAt : row.Seconds;
        DrawColumns(rect,
            ("RimAgent.Benchmark.Tier." + row.Tier).Translate() + "  " + ("RimAgent.Benchmark.Task." + row.Id).Translate(),
            ("RimAgent.Benchmark.State." + row.State).Translate(),
            started ? row.Tools.ToString(CultureInfo.InvariantCulture) + " / " + row.Failures.ToString(CultureInfo.InvariantCulture) : string.Empty,
            started ? TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", CultureInfo.InvariantCulture) : string.Empty,
            color);
        TooltipHandler.TipRegion(rect, string.IsNullOrEmpty(row.Detail) ? row.Instruction : row.Instruction + "\n\n" + row.Detail);
    }

    private static void DrawColumns(Rect rect, string task, string status, string tools, string time, Color statusColor)
    {
        Text.Anchor = TextAnchor.MiddleLeft;
        Text.WordWrap = false;
        Widgets.Label(new Rect(rect.x + 6f, rect.y, rect.width - 326f, rect.height), task);
        GUI.color = statusColor;
        Widgets.Label(new Rect(rect.xMax - 320f, rect.y, 100f, rect.height), status);
        GUI.color = Color.white;
        Widgets.Label(new Rect(rect.xMax - 215f, rect.y, 130f, rect.height), tools);
        Widgets.Label(new Rect(rect.xMax - 80f, rect.y, 80f, rect.height), time);
        Text.WordWrap = true;
        Text.Anchor = TextAnchor.UpperLeft;
    }
}

/// <summary>開跑前的確認：說明會動到什麼，並選這一輪的思考強度。</summary>
internal sealed class Window_BenchmarkStart : Window
{
    private readonly string _tier;
    private ReasoningLevel _level = Usable(BenchmarkComponent.Reasoning) ? BenchmarkComponent.Reasoning : ReasoningLevel.Medium;

    public Window_BenchmarkStart(string tier)
    {
        _tier = tier;
        doCloseX = true;
        forcePause = true;
        absorbInputAroundWindow = true;
        closeOnAccept = false;
    }

    public override Vector2 InitialSize => new(640f, 340f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 30f, 34f),
            "RimAgent.Benchmark.Title".Translate() + "  " + ("RimAgent.Benchmark.Tier." + (_tier ?? "all")).Translate());
        Text.Font = GameFont.Small;

        Rect buttons = new(inRect.x, inRect.yMax - 36f, inRect.width, 36f);
        Rect reasoning = new(inRect.x, buttons.y - 50f, inRect.width, 34f);
        Rect label = new(inRect.x, reasoning.y - 28f, inRect.width, 26f);
        Widgets.Label(new Rect(inRect.x, inRect.y + 42f, inRect.width, label.y - inRect.y - 46f), "RimAgent.Benchmark.Confirm".Translate());
        Widgets.Label(label, "RimAgent.Reasoning.Status".Translate(BenchmarkComponent.ReasoningLabel(_level)));
        DrawReasoning(reasoning);

        if (Widgets.ButtonText(buttons.LeftHalf().ContractedBy(4f, 0f), "CancelButton".Translate())) Close();
        if (Widgets.ButtonText(buttons.RightHalf().ContractedBy(4f, 0f), "RimAgent.Benchmark.Start".Translate()))
        {
            Close();
            BenchmarkComponent.Start(_tier, _level);
        }
    }

    private static bool Usable(ReasoningLevel level) => level != BenchmarkComponent.Routing || DecisionServiceFactory.CanUseDecisionModel();

    /// <summary>一排思考強度按鈕，附上該強度最近一輪的成績。</summary>
    private void DrawReasoning(Rect rect)
    {
        const float gap = 4f;
        ReasoningLevel[] levels = [BenchmarkComponent.Routing, .. Enum.GetValues<ReasoningLevel>()];
        float width = (rect.width - gap * (levels.Length - 1)) / levels.Length;
        Rect button = new(rect.x, rect.y, width, rect.height);
        foreach (ReasoningLevel level in levels)
        {
            string label = BenchmarkComponent.ReasoningLabel(level);
            string tip = "RimAgent.Benchmark.ReasoningTip".Translate();
            if (BenchmarkComponent.Scores.TryGetValue((_tier, level), out BenchmarkScore score))
            {
                label += string.Create(CultureInfo.InvariantCulture, $" {score.Passed}/{score.Total}");
                tip += "\n\n" + "RimAgent.Benchmark.Score".Translate(
                    score.Model,
                    ("RimAgent.Benchmark.Tier." + (_tier ?? "all")).Translate(),
                    score.Tools.ToString(CultureInfo.InvariantCulture) + " / " + score.Failures.ToString(CultureInfo.InvariantCulture),
                    TimeSpan.FromSeconds(score.Seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture),
                    score.Input.ToString("N0", CultureInfo.InvariantCulture),
                    score.Output.ToString("N0", CultureInfo.InvariantCulture));
            }
            bool usable = Usable(level);
            if (level == BenchmarkComponent.Routing)
                tip = (usable ? "RimAgent.Reasoning.RoutingTip" : "RimAgent.Benchmark.RoutingUnavailable").Translate() + "\n\n" + tip;
            if (Widgets.ButtonText(button, label, active: usable)) _level = level;
            if (_level == level) Widgets.DrawBox(button, 2, BaseContent.YellowTex);
            TooltipHandler.TipRegion(button, tip);
            button.x += width + gap;
        }
    }
}

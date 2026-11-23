using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

internal enum BenchmarkState
{
    Pending,
    Running,
    Passed,
    Failed
}

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
        int done = _rows.Count(r => r.State is BenchmarkState.Passed or BenchmarkState.Failed);
        int passed = _rows.Count(r => r.State == BenchmarkState.Passed);

        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 30f, 34f), "RimAgent.Benchmark.Title".Translate());
        Text.Font = GameFont.Small;

        Rect bar = new(inRect.x, inRect.y + 38f, inRect.width, 24f);
        Widgets.FillableBar(bar, _rows.Count == 0 ? 0f : (float)done / _rows.Count);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(bar, "RimAgent.Benchmark.Progress".Translate(done, _rows.Count, passed, done - passed));
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

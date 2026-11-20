using DevTools.Testing;
using RimAgent.UI;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>對話視窗繪製的回歸測試（需要遊戲的字型與 GUI 樣式，單元測試環境沒有）。DevTools 只註冊有一個公開建構式的類別，所以不宣告私有建構式。</summary>
[TestFixture(TestType.MainMenu)]
internal sealed class TranscriptUiTests
{
    /// <summary>
    /// 回歸：框選層取得焦點時曾改顯示純文字，與底下 markdown 排版的 Label 疊在一起（標題放大、粗體變寬，兩層對不齊）。
    /// 每個狀態（含焦點與按下）的文字都必須透明，畫面上只留 Label 與反白。
    /// </summary>
    [Test]
#pragma warning disable CA1822 // DevTools 要實例方法才能配合隱含的公開建構式註冊
    public void SelectableLayerTextIsTransparentInEveryState()
    {
        foreach (GameFont font in new[] { GameFont.Tiny, GameFont.Small, GameFont.Medium })
        {
            GameFont old = Text.Font;
            Text.Font = font;
            GUIStyle style = Window_Agent.GetSelectableStyle();
            Text.Font = old;
            foreach (GUIStyleState state in Window_Agent.SelectableStates(style))
            {
                Expect.AreEqual(Color.clear, state.textColor, font.ToString());
            }
        }
    }
#pragma warning restore CA1822
}

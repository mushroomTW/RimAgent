using UnityEngine;
using Verse;

namespace RimAgent.UI
{
    /// <summary>
    /// 常駐指令編輯窗：玩家寫給 agent 的長期守則，跟著存檔走，
    /// 每句指令的提示詞都附上（改完下一句生效，不必重開）。
    /// </summary>
    public class Dialog_StandingInstructions : Window
    {
        private string _input = string.Empty;

        public override Vector2 InitialSize => new Vector2(520f, 420f);

        public Dialog_StandingInstructions()
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            RimAgentGameComponent component = RimAgentGameComponent.Instance;
            if (component != null) _input = component.StandingInstructions;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            RimAgentGameComponent component = RimAgentGameComponent.Instance;
            if (component == null)
            {
                Close();
                return;
            }

            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("RimAgent.Standing.Title".Translate());
            listing.GapLine();

            Rect area = listing.GetRect(240f);
            _input = Widgets.TextArea(area, _input ?? string.Empty);
            listing.Gap(4f);
            listing.Label("(" + (_input ?? string.Empty).Length + "/" + RimAgentGameComponent.MaxStandingChars + ")");

            listing.Gap();
            Rect buttons = listing.GetRect(30f);
            float half = (buttons.width - 4f) / 2f;
            if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, half, buttons.height), "RimAgent.Standing.Save".Translate()))
            {
                component.SetStandingInstructions(_input);
                Close();
            }
            if (Widgets.ButtonText(new Rect(buttons.x + half + 4f, buttons.y, half, buttons.height), "RimAgent.Standing.Close".Translate()))
            {
                Close();
            }
            listing.End();
        }
    }
}

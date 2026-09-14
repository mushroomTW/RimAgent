using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimAgent.UI
{
    /// <summary>
    /// 長期目標管理視窗：玩家設長期目標，自主任務會附上並持續推進。
    /// 目標跟著存檔走（存在 GameComponent），跟設定頁的全局開關分開。
    /// </summary>
    public class Dialog_AutonomousGoals : Window
    {
        private string _input = string.Empty;

        public override Vector2 InitialSize => new Vector2(520f, 420f);

        public Dialog_AutonomousGoals()
        {
            doCloseX = true;
            closeOnClickedOutside = true;
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
            listing.Label("RimAgent.Goals.Title".Translate());
            listing.GapLine();

            IReadOnlyList<string> goals = component.Goals;
            if (goals.Count == 0)
            {
                GUI.color = Color.gray;
                listing.Label("RimAgent.Goals.Empty".Translate());
                GUI.color = Color.white;
            }
            for (int i = 0; i < goals.Count; i++)
            {
                Rect row = listing.GetRect(28f);
                Widgets.Label(new Rect(row.x, row.y, row.width - 70f, row.height), (i + 1) + ". " + goals[i]);
                if (Widgets.ButtonText(new Rect(row.xMax - 66f, row.y, 66f, row.height), "RimAgent.Goals.Remove".Translate()))
                {
                    component.RemoveGoalAt(i);
                    break;
                }
            }

            listing.Gap();
            Rect addRow = listing.GetRect(30f);
            _input = Widgets.TextField(new Rect(addRow.x, addRow.y, addRow.width - 110f, addRow.height), _input);
            if (addRow.width > 220f && Widgets.ButtonText(new Rect(addRow.xMax - 104f, addRow.y, 104f, addRow.height), "RimAgent.Goals.Add".Translate()))
            {
                if (component.AddGoal(_input)) _input = string.Empty;
            }

            listing.Gap();
            if (listing.ButtonText("RimAgent.Goals.Close".Translate())) Close();
            listing.End();
        }
    }
}

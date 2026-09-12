using UnityEngine;
using Verse;

namespace RimAgent
{
    /// <summary>Mod 進入點與設定頁。LLM 相關設定全在 RimLLM Framework，這裡只有工具權限與步數上限。</summary>
    public class RimAgentMod : Mod
    {
        public static RimAgentSettings Settings { get; private set; }

        public RimAgentMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimAgentSettings>();
        }

        public override string SettingsCategory()
        {
            return "RimAgent";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("RimAgent.Settings.PermissionsHeader".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("RimAgent.Settings.Observe".Translate(), ref Settings.allowObserve, "RimAgent.Settings.ObserveTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Work".Translate(), ref Settings.allowWork, "RimAgent.Settings.WorkTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Build".Translate(), ref Settings.allowBuild, "RimAgent.Settings.BuildTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Combat".Translate(), ref Settings.allowCombat, "RimAgent.Settings.CombatTip".Translate());

            listing.Gap();
            listing.Label("RimAgent.Settings.MaxSteps".Translate(Settings.maxSteps), -1f, "RimAgent.Settings.MaxStepsTip".Translate());
            Settings.maxSteps = Mathf.RoundToInt(listing.Slider(Settings.maxSteps, RimAgentSettings.MinSteps, RimAgentSettings.MaxSteps));

            listing.Gap();
            listing.Label("RimAgent.Settings.FrameworkNote".Translate());

            listing.End();
        }
    }
}

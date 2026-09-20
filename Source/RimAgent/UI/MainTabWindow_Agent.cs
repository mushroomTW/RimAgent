using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI
{
    /// <summary>
    /// 相容舊版 MainTabWindow 定義：若被開啟則轉導開啟懸浮視窗 <see cref="Window_Agent"/>。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "S101:TypesShouldBeNamedInPascalCase", Justification = "RimWorld MainTabWindow naming convention referenced in XML Defs")]
    public class MainTabWindow_Agent : MainTabWindow
    {
        public override Vector2 RequestedTabSize => new Vector2(10f, 10f);

        public MainTabWindow_Agent()
        {
            closeOnAccept = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            if (!Window_Agent.IsOpenNow)
            {
                Window_Agent.Toggle();
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Close(false);
        }
    }
}

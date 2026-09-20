using RimWorld;
using Verse;

namespace RimAgent.UI
{
    /// <summary>
    /// 底部工具列按鈕 Worker：點擊時切換懸浮 Agent 視窗的開啟/關閉狀態，不關閉世界地圖視角。
    /// </summary>
    public class MainButtonWorker_ToggleAgent : MainButtonWorker
    {
        public override void Activate()
        {
            Window_Agent.Toggle();
        }

        public override float ButtonBarPercent => Window_Agent.IsOpenNow ? 1f : 0f;
    }
}

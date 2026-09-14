using System.Collections.Generic;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;
using RimLLM_Framework;
using Verse;

namespace RimAgent
{
    /// <summary>
    /// 持有這一場遊戲的 <see cref="ColonyAgentSession"/>。玩家看得到的對話
    /// （指令、回覆、行動紀錄）寫進存檔，讀檔後仍在；AF session 的模型上下文
    /// 照舊歸零（等框架提供版本化的 session 序列化再評估），新局時兩者都清空，
    /// 避免把上一個殖民地的脈絡帶進來。RimWorld 會自動為每個 GameComponent 子類建立實例。
    /// </summary>
    public class RimAgentGameComponent : GameComponent
    {
        private ColonyAgentSession _session;
        private List<TranscriptEntryData> _loadedTranscript;

        // RimAgent.Game 命名空間會遮蔽 Verse.Game，這裡要寫全名。
        public RimAgentGameComponent(Verse.Game game)
        {
        }

        public static RimAgentGameComponent Instance => Current.Game?.GetComponent<RimAgentGameComponent>();

        /// <summary>延遲建立：框架的 client 要等 mod 全部載入完才拿得到。</summary>
        public ColonyAgentSession Session
        {
            get
            {
                if (_session == null)
                {
                    _session = CreateSession();
                    RestoreTranscript(_session);
                }
                // 步數上限即時生效：重設工具迴圈的屬性即可，AF session 的歷史不受影響。
                else if (RimAgentMod.Settings != null) _session.UpdateMaxSteps(RimAgentMod.Settings.maxSteps);
                return _session;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            List<TranscriptEntryData> data = null;
            // 新局與讀檔時 session 都是 null（延遲建立）；只有玩到一半存檔才有東西可寫。
            if (Scribe.mode == LoadSaveMode.Saving && _session != null)
            {
                data = TranscriptEntryData.FromTranscript(_session.Transcript);
            }
            Scribe_Collections.Look(ref data, "transcript", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) _loadedTranscript = data;
        }

        public override void StartedNewGame()
        {
            _session = null;
            _loadedTranscript = null;
        }

        public override void LoadedGame()
        {
            _session = null;
        }

        /// <summary>讀檔後第一次開分頁時，把存檔裡的對話倒回 transcript，並把指令編號推過舊紀錄。</summary>
        private void RestoreTranscript(ColonyAgentSession session)
        {
            List<TranscriptEntryData> loaded = _loadedTranscript;
            _loadedTranscript = null;
            if (loaded == null || loaded.Count == 0) return;
            int maxId = 0;
            foreach (TranscriptEntryData d in loaded)
            {
                if (d == null) continue;
                session.Transcript.Add(d.ToEntry());
                if (d.instructionId > maxId) maxId = d.instructionId;
            }
            session.Transcript.EnsureInstructionIdAtLeast(maxId);
        }

        private static ColonyAgentSession CreateSession()
        {
            IChatClient client = RimLLMProvider.CreateChatClient(ColonyAgentSession.ModId);
            RimAgentSettings settings = RimAgentMod.Settings;
            return new ColonyAgentSession(client, new RimWorldGameWorld(), () => settings.ToPermissions(), settings.maxSteps, () => settings.requireConfirmation);
        }
    }
}

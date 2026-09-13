using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;
using RimLLM_Framework;
using Verse;

namespace RimAgent
{
    /// <summary>
    /// 持有這一場遊戲的 <see cref="ColonyAgentSession"/>。不寫存檔；新局與讀檔時歸零，
    /// 避免把上一個殖民地的對話脈絡帶進來。RimWorld 會自動為每個 GameComponent 子類建立實例。
    /// </summary>
    public class RimAgentGameComponent : GameComponent
    {
        private ColonyAgentSession _session;

        // RimAgent.Game 命名空間會遮蔽 Verse.Game，這裡要寫全名。
        public RimAgentGameComponent(Verse.Game game)
        {
        }

        public static RimAgentGameComponent Instance => Current.Game?.GetComponent<RimAgentGameComponent>();

        /// <summary>延遲建立：框架的 client 要等 mod 全部載入完才拿得到。</summary>
        public ColonyAgentSession Session => _session ?? (_session = CreateSession());

        public override void StartedNewGame()
        {
            _session = null;
        }

        public override void LoadedGame()
        {
            _session = null;
        }

        private static ColonyAgentSession CreateSession()
        {
            IChatClient client = RimLLMProvider.CreateChatClient(ColonyAgentSession.ModId);
            RimAgentSettings settings = RimAgentMod.Settings;
            return new ColonyAgentSession(client, new RimWorldGameWorld(), () => settings.ToPermissions(), settings.maxSteps);
        }
    }
}

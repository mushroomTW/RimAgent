namespace RimAgent.Agent
{
    /// <summary>
    /// 前情提要本體住在 AgentPack/AGENTS.md（可免重編輯）；這裡只留一行保底：
    /// 擴充包遺失或損毀時模型至少知道自己是誰，不會拿著空系統提示詞燒 API。
    /// </summary>
    public static class AgentSystemPrompt
    {
        public const string FallbackPreamble =
@"You are RimAgent. You manage the player's RimWorld colony through tools; the player talks to you in the game's Agent tab.";
    }
}

namespace RimAgent.Tools
{
    /// <summary>
    /// 命令閘門：命令工具在動遊戲之前先問「這次可否執行」；不可時只回傳將做的事（預覽），
    /// 等玩家按下確認才真正執行。查詢不經閘門。閘門怎麼決定（模式、風險、分類器）
    /// 是 <see cref="Agent.ConfirmationGate"/> 的事，工具不必知道自己的風險等級。
    /// </summary>
    public interface ICommandGate
    {
        /// <summary>目前這次工具呼叫的命令可否執行。</summary>
        bool AllowCommands();

        /// <summary>記下一則被擋下的預覽，供 session 判斷是否要等玩家確認。</summary>
        void NoteBlocked(string preview);
    }
}

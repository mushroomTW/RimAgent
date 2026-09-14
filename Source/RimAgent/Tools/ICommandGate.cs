namespace RimAgent.Tools
{
    /// <summary>
    /// 命令閘門：「需確認」模式開啟時，Work／Build／Combat 類工具不直接執行命令，
    /// 只回傳將做的事（預覽），等玩家按下確認才真正執行。查詢不受影響。
    /// </summary>
    public interface ICommandGate
    {
        /// <summary>該類別的命令現在可否執行。</summary>
        bool AllowCommands(ToolCategory category);

        /// <summary>記下一則被擋下的預覽，供 session 判斷是否要等玩家確認。</summary>
        void NoteBlocked(string preview);
    }
}

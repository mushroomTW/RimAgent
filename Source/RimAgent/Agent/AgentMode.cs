namespace RimAgent.Agent
{
    /// <summary>
    /// 助手的執行模式，決定命令怎麼執行；查詢在任何模式都直接跑。
    /// 十二類工具權限仍是外層過濾（哪些工具存在），模式管內層（怎麼執行）。
    /// </summary>
    public enum AgentMode
    {
        /// <summary>唯讀：只有查詢工具交給模型，命令工具不存在。</summary>
        Ask,
        /// <summary>靜態標記為不可逆（<see cref="Tools.ToolRisk.Confirm"/>）的命令先預覽、玩家按確認才執行；其餘直接做。</summary>
        Confirm,
        /// <summary>每次命令先由分類器判定風險：低直接做，高走預覽→確認。</summary>
        Auto,
        /// <summary>完全託管：命令全部直接執行，排程器開啟；玩家仍可下指令。</summary>
        Autonomous
    }
}

namespace RimAgent.Agent;

/// <summary>
/// 助手的執行模式，決定命令怎麼執行；查詢在任何模式都直接跑。
/// 十二類工具權限仍是外層過濾（哪些工具存在），模式管內層（怎麼執行）。
/// </summary>
public enum AgentMode
{
    /// <summary>唯讀：只有查詢工具交給模型，命令工具不存在。</summary>
    Ask,
    /// <summary>確認：每個指令都需要玩家手動確認。</summary>
    Confirm,
    /// <summary>舊版設定值；載入時改為確認模式。</summary>
    Yolo,
    /// <summary>自動：每次命令先由分類模型判定，僅核可的低風險命令執行，其餘拒絕並說明原因。</summary>
    Auto,
    /// <summary>完整權限：已啟用的工具不受模式限制。背景照顧另行設定。</summary>
    Autonomous,
    /// <summary>執行一般操作；重大操作先取得玩家確認。</summary>
    Execute
}

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
    /// <summary>YOLO：夾在確認與自動之間，無排程器，受黑名單保護；通用 gizmo／右鍵／對話選項仍需逐次確認，其餘命令（含 MCP）免確認直接執行。</summary>
    Yolo,
    /// <summary>自動：每次命令先由分類模型判定，僅核可的低風險命令執行，其餘拒絕並說明原因。</summary>
    Auto,
    /// <summary>自主：後台排程器自主運轉，不限制任何行為（無黑名單限制），不設預算上限。</summary>
    Autonomous
}

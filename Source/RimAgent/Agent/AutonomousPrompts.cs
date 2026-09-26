namespace RimAgent.Agent
{
    /// <summary>
    /// 自主任務的指令文字；goal 由獨立流程續跑，不混入事件排程。
    /// 文案住在 AgentPack/Prompts/Autonomous.en.md（`## 觸發原因` 章節，只留英文），
    /// 這裡只剩組裝；單元測試不進遊戲，Translate 在此無效，所以走檔案而非語言檔。
    /// 第一個章節之前的是共用通則，會接在每個任務前面送出。
    /// 任務找不到（缺檔或缺章節）回傳 null：呼叫端跳過本輪，而不是送一句空話燒 API。
    /// </summary>
    public static class AutonomousPrompts
    {
        public static string Build(AutonomousPack pack, AutonomousTrigger trigger)
        {
            if (pack == null) return null;
            string task;
            if (pack.Tasks == null || !pack.Tasks.TryGetValue(trigger.ToString(), out task) || string.IsNullOrWhiteSpace(task))
            {
                if (pack.Tasks == null || !pack.Tasks.TryGetValue(AutonomousTrigger.Interval.ToString(), out task) || string.IsNullOrWhiteSpace(task)) return null;
            }
            // 共用通則一定要跟著送：像「沒緊急狀況也要產出、不要只回報」這種規則
            // 寫在檔頭比在十三個章節各寫一次好，漏送就等於規則不存在。
            string preamble = pack.Preamble;
            if (string.IsNullOrWhiteSpace(preamble)) return task;
            return preamble.Trim() + "\n\n" + task;
        }

        /// <summary>該觸發有無專屬章節（呼叫端藉此判斷是否退回 Interval，退回時警告一次讓拼字錯誤可見）。</summary>
        public static bool HasTask(AutonomousPack pack, AutonomousTrigger trigger)
        {
            if (pack?.Tasks == null) return false;
            string task;
            return pack.Tasks.TryGetValue(trigger.ToString(), out task) && !string.IsNullOrWhiteSpace(task);
        }

    }
}

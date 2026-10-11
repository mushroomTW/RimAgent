using System.Collections.Generic;

namespace RimAgent.Game
{
    /// <summary>
    /// 工具與遊戲之間唯一的接縫。只用純資料型別，不外露任何 Verse / RimWorld 型別。
    /// 正式實作是薄轉接層（每個方法只做對應的 RimWorld API 呼叫，不含決策邏輯），
    /// 並假設呼叫者已在 Unity 主線程；測試以記憶體內的假世界取代。
    /// 命令一律回傳 <see cref="CommandResult"/> 而不丟例外——例外只代表 bug。
    /// </summary>
    public interface IGameWorld
    {
        ColonyOverview GetOverview();
        /// <summary>當前地圖上敵對目標的數量（與概況的 HostileCount 同一算法，但不必算整份概況）。</summary>
        int GetHostileCount();
        /// <summary>當前地圖上處於急診的殖民者 id（倒地或流血危及生命，不含麻醉中；與排程器急診訊號同一判定）。</summary>
        IReadOnlyList<int> GetColonistsInMedicalEmergency();
        /// <summary>當前地圖上燃燒中的火數。</summary>
        int GetFireCount();
        IReadOnlyList<ColonistInfo> GetColonists();
        IReadOnlyList<PrisonerInfo> GetPrisoners();
        /// <summary>地圖上的動物（殖民地與野生，見 AnimalInfo.Wild）。</summary>
        IReadOnlyList<AnimalInfo> GetAnimals();
        IReadOnlyList<ZoneInfo> GetZones();
        IReadOnlyList<WorkTableInfo> GetWorkTables();
        /// <summary>一張工作台的帳單（未知工作台回傳空表）。</summary>
        IReadOnlyList<BillInfo> GetBills(int tableId);
        IReadOnlyList<ColonistHealthInfo> GetHealth();
        ResourceSnapshot GetResources();
        IReadOnlyList<AlertInfo> GetAlerts();
        IReadOnlyList<LetterInfo> GetRecentLetters();
        /// <summary>等玩家回應的抉擇信件與開著的對話框、確認框（一般通知、死訊與任務邀請不算）。</summary>
        IReadOnlyList<PendingChoiceInfo> GetPendingChoices();
        PowerInfo GetPower();
        IReadOnlyList<ThreatInfo> GetThreats();
        ResearchInfo GetResearch();
        /// <summary>目前的遊戲時間流速（含可切換的速度目錄）。</summary>
        TimeSpeedInfo GetTimeSpeed();
        /// <summary>存檔裡的所有地圖（含不在當前地圖的殖民地與商隊所在地）。</summary>
        IReadOnlyList<MapInfo> GetMaps();
        /// <summary>當前地圖可交易的商人（軌道商人與來訪商隊，見 TraderInfo.Kind）。</summary>
        IReadOnlyList<TraderInfo> GetTraders();
        IReadOnlyList<ThingInfo> FindThings(ThingQuery query);
        /// <summary>玩家目前框選的地圖物件。</summary>
        IReadOnlyList<ThingInfo> GetSelection();
        MapBounds GetMapBounds();
        /// <summary>一格的地面狀態；呼叫者已確認座標在地圖內。</summary>
        CellInfo GetCell(int x, int z);
        /// <summary>矩形內每一格的地面狀態（含邊界）；呼叫者已確認矩形在地圖內並限制大小。</summary>
        IReadOnlyList<CellInfo> GetCells(CellRectSpec rect);

        /// <summary>
        /// 遊戲目前不能執行工具的原因（沒有進行中的遊戲、讀檔或生成地圖中、沒有當前地圖）；可以執行時回傳 null。
        /// </summary>
        string GetNotReadyReason();

        /// <summary>
        /// 讓遊戲時間推進 <paramref name="ticks"/> tick 後才完成（不阻塞主線程，遊戲照常跑）。
        /// 遊戲暫停（含對話框強制暫停）或不能執行工具時立即以失敗結束，已推進的部分不回溯。
        /// </summary>
        System.Threading.Tasks.Task<CommandResult> WaitTicksAsync(int ticks, System.Threading.CancellationToken cancellationToken);

        /// <summary>
        /// 取走自上次呼叫以來遊戲記錄的錯誤（Log.Error）訊息，每則一行；沒有新錯誤回傳空表。
        /// 只看主線程可見的訊息佇列，不攔截遊戲。
        /// </summary>
        IReadOnlyList<string> TakeNewGameErrors();

        // Def 目錄：供解析器把玩家／模型給的名稱對到 defName。
        IReadOnlyList<DefInfo> WorkTypes { get; }
        IReadOnlyList<DefInfo> Buildables { get; }
        IReadOnlyList<DefInfo> Stuffs { get; }
        IReadOnlyList<DefInfo> TimeAssignments { get; }
        /// <summary>可交易的物品目錄（銀兩除外，銀兩自動結算）。</summary>
        IReadOnlyList<DefInfo> TradeGoods { get; }
        /// <summary>五種醫療等級目錄（NoCare … Best）。</summary>
        IReadOnlyList<DefInfo> MedicalCareLevels { get; }
        /// <summary>囚犯互動模式目錄（PrisonerInteractionModeDef，如 AttemptRecruit）。</summary>
        IReadOnlyList<DefInfo> PrisonerInteractionModes { get; }
        /// <summary>可訓練項目目錄（TrainableDef，如 Obedience）。</summary>
        IReadOnlyList<DefInfo> AnimalTraining { get; }
        /// <summary>動物可指派的區域目錄（另有無限制，不在目錄內）。</summary>
        IReadOnlyList<DefInfo> AnimalAreas { get; }
        /// <summary>儲藏篩選可開關的品項目錄（物品類 ThingDef）。</summary>
        IReadOnlyList<DefInfo> StorageFilterables { get; }
        /// <summary>種植區可指定的作物目錄（可播種的植物 ThingDef，供 add_zone 把名稱對到 defName）。</summary>
        IReadOnlyList<DefInfo> GrowablePlants { get; }
        /// <summary>這張工作台可做的配方（有產出物的 RecipeDef，不含手術）；找不到工作台回傳空清單。</summary>
        IReadOnlyList<DefInfo> GetTableRecipes(int tableId);
        /// <summary>可排程的手術目錄（worker 為手術的 RecipeDef）。</summary>
        IReadOnlyList<DefInfo> SurgeryRecipes { get; }
        /// <summary>尚未完成的研發目錄（供 set_research 把名稱對到 defName）。</summary>
        IReadOnlyList<DefInfo> ResearchProjects { get; }
        /// <summary>已完成的研發目錄（set_research 對不到未完成專案時，用來回報「已經研究完」而不是「找不到」）。</summary>
        IReadOnlyList<DefInfo> FinishedResearchProjects { get; }
        /// <summary>可切換的時間流速目錄（供 set_time_speed 把名稱對到 defName）。</summary>
        IReadOnlyList<DefInfo> TimeSpeeds { get; }

        CommandResult SetWorkPriority(int pawnId, string workTypeDefName, int priority);
        CommandResult SetTimeAssignment(int pawnId, int hour, string assignmentDefName);
        CommandResult AddDesignationOnCell(int x, int z, string designationDefName);
        CommandResult AddDesignationOnThing(int thingId, string designationDefName);
        CommandResult RemoveDesignationOnCell(int x, int z, string designationDefName);
        CommandResult RemoveDesignationOnThing(int thingId, string designationDefName);
        /// <summary>內含可放置檢查；失敗訊息帶遊戲給的原因。</summary>
        CommandResult TryPlaceBlueprint(string buildableDefName, int x, int z, int rotation, string stuffDefName);
        CommandResult SetDrafted(int pawnId, bool drafted);
        CommandResult OrderMoveTo(int pawnId, int x, int z);
        CommandResult OrderAttack(int pawnId, int targetId);
        /// <summary>
        /// 回應一個抉擇：信件照玩家流程先打開再按選項；option 為 1 起的序號。
        /// 成功時 Message 為選到的選項文字；對話框可能接著出現下一段（再查 GetPendingChoices）。
        /// </summary>
        CommandResult ChooseOption(string choiceId, int option);
        /// <summary>
        /// 任務分頁的任務：可接受（含被拒絕、不含已過期）與進行中，不含已結束與隱藏的。
        /// </summary>
        IReadOnlyList<QuestInfo> GetQuests();
        /// <summary>
        /// 照任務分頁的接受鈕接受任務。rewardNumber 為獎勵選項序號（沒有選項或只有一組時可為 0），
        /// accepterPawnId 為接受者（任務不需要接受者時為 0）。條件不滿足、已過期、接受者不合格都回失敗並帶遊戲的原因。
        /// 成功時 Message 為接受明細。
        /// </summary>
        CommandResult AcceptQuest(int questId, int rewardNumber, int accepterPawnId);
        /// <summary>照任務分頁的拒絕鈕拒絕或取消拒絕一個尚未接受的任務（任務照常到期，只是從可接受頁收起來）。</summary>
        CommandResult SetQuestDismissed(int questId, bool dismissed);
        /// <summary>物件目前看得到的指令按鈕（不含開發者模式按鈕）；找不到或在迷霧裡的物件回傳 null。</summary>
        IReadOnlyList<GizmoInfo> GetGizmos(int thingId);
        /// <summary>
        /// 按一個指令按鈕（依 1 起的序號定位並核對標籤），等同玩家點它。只支援按一下與開關；
        /// 按了開出選單或其他視窗會關回去並回報失敗，開出對話框或確認框則留著給 ChooseOption 回應。
        /// 成功時 Message 為開關的新狀態（action 為空字串）。
        /// </summary>
        CommandResult UseGizmo(int thingId, int gizmoNumber, string label);
        /// <summary>
        /// 對一名殖民者下達一次性的工作命令（job：rescue、capture、tend、strip、equip、enter_casket），檢查照遊戲右鍵選單。
        /// 成功時 Message 為做什麼（例如把誰送去哪張床、用哪種藥）。
        /// </summary>
        CommandResult OrderJob(int pawnId, string job, int targetId);
        /// <summary>
        /// 禁用或解禁範圍內（null 為全圖）看得到的可搬運物品與屍體；labelContains 空字串代表不限。
        /// 成功時 Message 為改動的數量與品項。
        /// </summary>
        CommandResult SetForbidden(CellRectSpec? area, string labelContains, bool forbidden);
        /// <summary>一次結清的交易：驗證不過整筆不做，成功時 Message 為交易明細。</summary>
        CommandResult ExecuteTrade(TradeRequest request);
        /// <summary>組織商隊外出：驗證不過不組，成功時 Message 為成團明細。</summary>
        CommandResult FormCaravan(CaravanRequest request);
        CommandResult SetMedicalCare(int pawnId, string careDefName);
        /// <summary>切換囚犯互動模式（走獄卒原生機制，不硬轉派系）。</summary>
        CommandResult SetPrisonerInteraction(int pawnId, string modeDefName);
        /// <summary>立刻釋放囚犯（等同遊戲原生釋放）。</summary>
        CommandResult ReleasePrisoner(int pawnId);
        /// <summary>標記馴養野生動物（走遊戲原生 Tame designation）。</summary>
        CommandResult TameAnimal(int pawnId);
        /// <summary>開關一項動物訓練。</summary>
        CommandResult SetAnimalTraining(int pawnId, string trainableDefName, bool enable);
        /// <summary>指派動物活動區域；空字串代表無限制。</summary>
        CommandResult SetAnimalArea(int pawnId, string areaLabel);
        /// <summary>新增儲藏／種植區：矩形含邊界，任一格越界或已有區域即整筆不做。</summary>
        CommandResult AddZone(string kind, CellRectSpec rect, string plantDefName);
        /// <summary>刪除區域。</summary>
        CommandResult DeleteZone(int zoneId);
        /// <summary>開關儲藏區允收品項；兩個清單都空即報錯。</summary>
        CommandResult SetZoneFilter(int zoneId, IReadOnlyList<string> allowDefNames, IReadOnlyList<string> disallowDefNames);
        /// <summary>工作台加一張生產帳單（做 count 次）。</summary>
        CommandResult AddBill(int tableId, string recipeDefName, int count);
        /// <summary>刪工作台帳單（index 為列表序號，1 起）。</summary>
        CommandResult DeleteBill(int tableId, int index);
        /// <summary>排一台手術：驗證不過不掛單，成功時 Message 為排程明細。</summary>
        CommandResult ScheduleSurgery(SurgeryRequest request);
        /// <summary>
        /// 切換進行中的研發：驗證不過不切換。defName 為空、"none" 或 "stop" 代表停下目前研發
        /// （不斷 本次存檔的研究進度，下次可再切回來）。
        /// </summary>
        CommandResult SetResearch(string researchDefName);
        /// <summary>切換工具操作的當前地圖（等同玩家切換視角的地圖）。</summary>
        CommandResult SetCurrentMap(int mapId);
        /// <summary>切換遊戲時間流速（暫停／正常／加速）。</summary>
        CommandResult SetTimeSpeed(string speedDefName);
        /// <summary>擷取當前遊戲畫面並存檔。filename 為可選檔名（不含路徑與副檔名）；空值則自動以時間戳記命名。</summary>
        ScreenshotInfo TakeScreenshot(string filename = null);
    }
}

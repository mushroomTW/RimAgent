#nullable disable

using System.Collections.Generic;

namespace RimAgent.Game;

/// <summary>工具與遊戲之間唯一的接縫，只用純資料型別；假設呼叫者已在 Unity 主線程，命令一律回傳 <see cref="CommandResult"/> 而不丟例外（例外只代表 bug）。</summary>
public interface IGameWorld
{
    ColonyOverview GetOverview();
    int GetHostileCount();
    IReadOnlyList<int> GetColonistsInMedicalEmergency();
    int GetFireCount();
    IReadOnlyList<ColonistInfo> GetColonists();
    IReadOnlyList<PrisonerInfo> GetPrisoners();
    IReadOnlyList<AnimalInfo> GetAnimals();
    IReadOnlyList<ZoneInfo> GetZones();
    IReadOnlyList<WorkTableInfo> GetWorkTables();
    IReadOnlyList<BillInfo> GetBills(int tableId);
    IReadOnlyList<ColonistHealthInfo> GetHealth();
    ResourceSnapshot GetResources();
    IReadOnlyList<AlertInfo> GetAlerts();
    IReadOnlyList<LetterInfo> GetRecentLetters();
    IReadOnlyList<PendingChoiceInfo> GetPendingChoices();
    PowerInfo GetPower();
    IReadOnlyList<ThreatInfo> GetThreats();
    ResearchInfo GetResearch();
    TimeSpeedInfo GetTimeSpeed();
    IReadOnlyList<MapInfo> GetMaps();
    IReadOnlyList<TraderInfo> GetTraders();
    /// <summary>
    /// 收集全部符合的物件後排序再取前 Limit 筆：未被禁用的在前，同組內依距離殖民者平均位置由近到遠
    /// （地圖上沒有殖民者時以地圖中心為準）。TotalMatches 是排序前的符合總數。
    /// </summary>
    ThingSearchResult FindThings(ThingQuery query);
    IReadOnlyList<ThingInfo> GetSelection();
    MapBounds GetMapBounds();
    CellInfo GetCell(int x, int z);
    IReadOnlyList<CellInfo> GetCells(CellRectSpec rect);
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

    IReadOnlyList<DefInfo> WorkTypes { get; }
    IReadOnlyList<DefInfo> Buildables { get; }
    IReadOnlyList<DefInfo> Stuffs { get; }
    IReadOnlyList<DefInfo> TimeAssignments { get; }
    IReadOnlyList<DefInfo> TradeGoods { get; }
    IReadOnlyList<DefInfo> MedicalCareLevels { get; }
    IReadOnlyList<DefInfo> PrisonerInteractionModes { get; }
    IReadOnlyList<DefInfo> AnimalTraining { get; }
    IReadOnlyList<DefInfo> AnimalAreas { get; }
    IReadOnlyList<DefInfo> StorageFilterables { get; }
    IReadOnlyList<DefInfo> GrowablePlants { get; }
    IReadOnlyList<DefInfo> GetTableRecipes(int tableId);
    IReadOnlyList<DefInfo> SurgeryRecipes { get; }
    IReadOnlyList<DefInfo> ResearchProjects { get; }
    IReadOnlyList<DefInfo> FinishedResearchProjects { get; }
    IReadOnlyList<DefInfo> TimeSpeeds { get; }

    /// <summary>殖民地目前是否使用手動工作優先級；set_work_priorities 可能為了 1/2/4 自動開啟它，復原時要能還原。</summary>
    bool UseWorkPriorities { get; }
    void SetUseWorkPriorities(bool value);
    CommandResult SetWorkPriority(int pawnId, string workTypeDefName, int priority);
    CommandResult SetTimeAssignment(int pawnId, int hour, string assignmentDefName);
    CommandResult AddDesignationOnCell(int x, int z, string designationDefName);
    IReadOnlyCollection<int> WithoutStorage(IReadOnlyCollection<int> thingIds);
    CommandResult AddDesignationOnThing(int thingId, string designationDefName);
    CommandResult RemoveDesignationOnCell(int x, int z, string designationDefName);
    CommandResult RemoveDesignationOnThing(int thingId, string designationDefName);
    BuildingInfo GetBuildingInfo(string buildableDefName);
    IReadOnlyList<DefInfo> GetLookupCatalog(string kind);
    DefLookupInfo LookupDef(string kind, string defName);
    CommandResult TryPlaceBlueprint(string buildableDefName, int x, int z, int rotation, string stuffDefName, bool checkOnly = false);
    IReadOnlyList<LayoutInfo> Layouts { get; }
    LayoutPlan GetLayoutPlan(string layoutDefName, int x, int z, int rotation);
    IReadOnlyList<string> RemoveBlueprints(CellRectSpec rect);
    IReadOnlyList<ShipPartInfo> GetShipParts();
    IReadOnlyList<ShipPartRequirement> GetRequiredShipParts();
    CommandResult SetDrafted(int pawnId, bool drafted);
    CommandResult OrderMoveTo(int pawnId, int x, int z);
    CommandResult OrderAttack(int pawnId, int targetId);
    CommandResult ChooseOption(string choiceId, int option, string input = null);
    string GetInspectText(int thingId);
    IReadOnlyList<QuestInfo> GetQuests();
    CommandResult AcceptQuest(int questId, int rewardNumber, int accepterPawnId);
    CommandResult SetQuestDismissed(int questId, bool dismissed);
    IReadOnlyList<GizmoInfo> GetGizmos(int thingId);
    /// <summary>
    /// 按一個指令按鈕（依 1 起的序號定位並核對標籤），等同玩家點它。支援按一下、開關與需要地圖目標的按鈕；
    /// 按了開出選單或其他視窗會關回去並回報失敗，開出對話框或確認框則留著給 ChooseOption 回應。
    /// 按鈕開始選目標時：給了 target 就照遊戲的選目標器同一套檢查（可選的種類、可否命中、遊戲自己的拒絕原因）
    /// 驗證後下令，不合法回失敗並帶原因；沒給 target 則取消選目標並回失敗，說明要什麼目標。
    /// 成功時 Message 為開關的新狀態或目標與遊戲訊息（單純 action 為空字串）。
    /// </summary>
    CommandResult UseGizmo(int thingId, int gizmoNumber, string label, MapTarget target);
    /// <summary>
    /// 殖民者對某目標（物件或格子）按右鍵得到的選單，照原版 FloatMenuMakerMap 產生；
    /// 殖民者不存在、無法下令或目標不在地圖上時 Error 帶原因。
    /// </summary>
    FloatMenuInfo GetFloatMenuOptions(int colonistId, MapTarget target);
    /// <summary>
    /// 執行右鍵選單的一個選項（重新產生選單，依 1 起的序號定位並核對標籤，等同玩家點它）。
    /// 選項開出對話框或確認框時留著給 ChooseOption 回應；開出選目標器或其他視窗則關回去並回報失敗。
    /// 成功時 Message 為遊戲回報的訊息（可為空字串）。
    /// </summary>
    CommandResult UseFloatMenuOption(int colonistId, MapTarget target, int optionNumber, string label);
    CommandResult OrderJob(int pawnId, string job, int targetId);
    CommandResult SetForbidden(CellRectSpec? area, string labelContains, bool forbidden);
    CommandResult ExecuteTrade(TradeRequest request);
    IReadOnlyList<CaravanCargoInfo> GetCaravanCargo();
    CaravanPlanInfo PlanCaravan(CaravanRequest request);
    /// <summary>
    /// 組織商隊：等同在組隊視窗按「送出」（CaravanFormingUtility.StartFormingCaravan）——成員先到集合點收齊貨物，
    /// 再一起走出地圖邊緣；要花遊戲時間。原版會拒絕的情況（超重、沒選目的地、找不到出口…）不組並回報原因，
    /// 成功時 Message 為集結明細。
    /// </summary>
    CommandResult FormCaravan(CaravanRequest request);
    IReadOnlyList<FormingCaravanInfo> GetFormingCaravans();
    CommandResult CancelFormingCaravan(int formingId);
    IReadOnlyList<CaravanInfo> GetCaravans();
    /// <summary>
    /// 世界上值得注意的物件（定居點、據點、營地、其他派系商隊…，不含玩家商隊本身），距離從起點量。
    /// originCaravanId 為商隊編號，-1 代表從當前地圖所在的格子量。清單不分頁，篩選與排序由呼叫端做。
    /// </summary>
    WorldSurveyInfo GetWorldSurvey(int originCaravanId);
    /// <summary>
    /// 從起點到各格的行程估算，用遊戲自己的路徑與到達時間估算；起點同 <see cref="GetWorldSurvey"/>。
    /// 每格各跑一次尋路，呼叫端只該問要顯示的幾格。
    /// </summary>
    IReadOnlyList<TravelEstimateInfo> GetTravelEstimates(int originCaravanId, IReadOnlyList<int> tiles);
    WorldTileInfo GetWorldTile(int tile, int originCaravanId);
    CaravanOptionsInfo GetCaravanOptions(int caravanId, int tile);
    CommandResult SendCaravan(int caravanId, int tile, int optionNumber, string label);
    CommandResult SetCaravanMovement(int caravanId, string action);
    /// <summary>
    /// 商隊就地建立新殖民地或搭營地（<see cref="CaravanSettleAction"/>），照原版商隊按鈕：同樣的可否判斷與確認框。
    /// 地圖在背景生成，回傳時還沒完成；原版會跳出的確認框留著給 ChooseOption 回應。
    /// </summary>
    CommandResult SettleCaravan(int caravanId, string action);
    CommandResult SetMedicalCare(int pawnId, string careDefName);
    CommandResult SetPrisonerInteraction(int pawnId, string modeDefName);
    CommandResult ReleasePrisoner(int pawnId);
    CommandResult TameAnimal(int pawnId);
    CommandResult SetAnimalTraining(int pawnId, string trainableDefName, bool enable);
    CommandResult SetAnimalArea(int pawnId, string areaLabel);
    /// <summary>新增儲藏／種植區：矩形含邊界，任一格越界或已有區域即整筆不做。</summary>
    CommandResult AddZone(string kind, CellRectSpec rect, string plantDefName);
    CommandResult DeleteZone(int zoneId);
    CommandResult SetZoneFilter(int zoneId, IReadOnlyList<string> allowDefNames, IReadOnlyList<string> disallowDefNames);
    CommandResult AddBill(int tableId, string recipeDefName, int count);
    CommandResult DeleteBill(int tableId, int index);
    CommandResult ScheduleSurgery(SurgeryRequest request);
    /// <summary>
    /// 切換進行中的研發：驗證不過不切換。defName 為空、"none" 或 "stop" 代表停下目前研發
    /// （不斷 本次存檔的研究進度，下次可再切回來）。
    /// </summary>
    CommandResult SetResearch(string researchDefName);
    CommandResult SetCurrentMap(int mapId);
    CommandResult SetTimeSpeed(string speedDefName);
    ScreenshotInfo TakeScreenshot(string filename = null);
    /// <summary>
    /// 不動玩家鏡頭，把目前地圖的一塊範圍以俯視角離屏渲染，加上座標格線後存成 PNG（版面見 <see cref="MapShotLayout"/>）。
    /// 範圍需已裁進地圖內。檔案在之後一兩幀渲染完才寫出，回傳的路徑可交給 <see cref="ReadScreenshotFile"/> 重試讀取。
    /// </summary>
    ScreenshotInfo RenderMapArea(CellRectSpec rect, string filename = null);
    MapSurvey GetMapSurvey();
    /// <summary>
    /// 讀取遊戲截圖資料夾中的 PNG 原始檔，可在背景執行緒呼叫（解碼與縮圖由呼叫端排回主線程）。
    /// 檔案不在截圖資料夾或尚未寫完時回傳空陣列（截圖在該幀渲染完才寫出，呼叫端可稍後重試）。
    /// </summary>
    byte[] ReadScreenshotFile(string filePath);

    SaveContext GetSaveContext();
    /// <summary>
    /// RimAgent 檢查點與遊戲 autosave 的清單（含存檔裡的殖民地代號與 tick）；
    /// 讀不出代號的 autosave 照樣列出、代號為空。讀檔案在背景執行緒，不卡主線程。
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<SaveSlotInfo>> GetSaveFilesAsync();
    /// <summary>排入存檔（下一幀寫入，同原版存檔視窗）。</summary>
    CommandResult SaveGame(string saveName);
    CommandResult DeleteCheckpoint(string saveName);
    /// <summary>停掉這一局的 agent 工作並排入讀檔；回傳後整場遊戲會在下一幀被換掉。</summary>
    CommandResult LoadGame(string saveName);
}

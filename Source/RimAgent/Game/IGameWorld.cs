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
        IReadOnlyList<ColonistInfo> GetColonists();
        IReadOnlyList<PrisonerInfo> GetPrisoners();
        /// <summary>地圖上的動物（殖民地與野生，見 AnimalInfo.Wild）。</summary>
        IReadOnlyList<AnimalInfo> GetAnimals();
        IReadOnlyList<ColonistHealthInfo> GetHealth();
        ResourceSnapshot GetResources();
        IReadOnlyList<ThreatInfo> GetThreats();
        ResearchInfo GetResearch();
        /// <summary>當前地圖可交易的商人（軌道商人與來訪商隊，見 TraderInfo.Kind）。</summary>
        IReadOnlyList<TraderInfo> GetTraders();
        IReadOnlyList<ThingInfo> FindThings(ThingQuery query);
        /// <summary>玩家目前框選的地圖物件。</summary>
        IReadOnlyList<ThingInfo> GetSelection();
        MapBounds GetMapBounds();

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
        /// <summary>可排程的手術目錄（worker 為手術的 RecipeDef）。</summary>
        IReadOnlyList<DefInfo> SurgeryRecipes { get; }

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
        /// <summary>一次結清的買賣：驗證不過整筆不做，成功時 Message 為買賣明細。</summary>
        CommandResult ExecuteTrade(TradeRequest request);
        /// <summary>組商隊外出：驗證不過不組，成功時 Message 為成團明細。</summary>
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
        /// <summary>排一台手術：驗證不過不掛單，成功時 Message 為排程明細。</summary>
        CommandResult ScheduleSurgery(SurgeryRequest request);
    }
}

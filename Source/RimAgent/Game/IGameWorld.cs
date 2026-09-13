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
    }
}

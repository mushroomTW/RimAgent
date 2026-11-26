#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public ColonyOverview GetOverview()
    {
        Map map = Map;
        List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
        return new ColonyOverview
        {
            ColonyName = map.Parent?.Label ?? Faction.OfPlayer?.Name ?? string.Empty,
            ColonistCount = colonists.Count,
            Year = GenLocalDate.Year(map),
            Season = GenLocalDate.Season(map).ToString(),
            DayOfSeason = GenLocalDate.DayOfSeason(map),
            Hour = GenLocalDate.HourInteger(map),
            Weather = map.weatherManager.curWeather?.label ?? string.Empty,
            OutdoorTemperatureC = map.mapTemperature.OutdoorTemp,
            Wealth = map.wealthWatcher.WealthTotal,
            Factions = [.. Find.FactionManager.AllFactionsVisible.Where(f => !f.IsPlayer)
                .Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Name} {f.PlayerGoodwill:+0;-0;0} ({f.PlayerRelationKind.GetLabel()})"))],
            HostileCount = CountHostiles(map),
            ColonistsNearMentalBreak = colonists.Count(IsNearMentalBreak)
        };
    }

    private struct ColonistSignalsAccumulator
    {
        public int ColonistCount;
        public int NearMentalBreak;
        public int DownedOrBleeding;

        public void AccumulateColonists(List<Pawn> colonists)
        {
            if (colonists == null) return;
            for (int p = 0; p < colonists.Count; p++)
            {
                AccumulateColonist(colonists[p]);
            }
        }

        public void AccumulateColonist(Pawn pawn)
        {
            // 每 500 tick 跑一次，外層抓到例外就整次作廢：其他模組的 pawn 一丟例外自主模式就停擺。
            // 逐個隔離，全部讀完才一起計入，壞掉的那一位整個不算，不留半套計數。
            bool nearBreak;
            bool downed;
            try
            {
                nearBreak = IsNearMentalBreak(pawn);
                downed = IsMedicalEmergency(pawn);
            }
            catch (Exception ex)
            {
                WarnModError(pawn, "autonomous signals", ex);
                return;
            }
            ColonistCount++;
            if (nearBreak) NearMentalBreak++;
            if (downed) DownedOrBleeding++;
        }
    }

    /// <summary>
    /// 自主排程器每 500 tick 要的數字，不建完整快照（GetOverview／GetResources 會為每個威脅與資源配置物件）。
    /// 除了應急訊號，也收遊戲目前的警報，讓排程器看出基地在慢慢壞掉。
    /// </summary>
    internal static Agent.AutonomousSignals GetAutonomousSignals()
    {
        List<Map> maps = Find.Maps;
        int hostileCount = 0;
        float foodNutrition = 0f;
        int fireCount = 0;
        var accumulator = new ColonistSignalsAccumulator();
        List<(string Label, bool Critical)> alerts = [.. ActiveAlerts().Select(a => (a.Label, a.Priority == AlertPriority.Critical))];

        if (maps != null)
        {
            for (int m = 0; m < maps.Count; m++)
            {
                AccumulateMapSignals(maps[m], ref hostileCount, ref foodNutrition, ref fireCount, ref accumulator);
            }
        }

        return new Agent.AutonomousSignals
        {
            HostileCount = hostileCount,
            NearMentalBreakCount = accumulator.NearMentalBreak,
            FoodNutrition = foodNutrition,
            ColonistCount = accumulator.ColonistCount,
            DownedOrBleedingCount = accumulator.DownedOrBleeding,
            FireCount = fireCount,
            AlertCount = alerts.Count(a => !a.Critical),
            CriticalAlertCount = alerts.Count(a => a.Critical),
            Alerts = string.Join("; ", alerts.Select(a => (a.Critical ? "[Critical] " : string.Empty) + a.Label))
        };
    }

    private static void AccumulateMapSignals(Map map, ref int hostileCount, ref float foodNutrition, ref int fireCount, ref ColonistSignalsAccumulator accumulator)
    {
        if (map == null) return;
        // 地圖層的讀數不包 try：讀一半就把食物當 0 會造成假警報，寧可整次作廢。
        // 會跑到其他模組程式的攻擊目標已在 CountHostiles 裡逐個隔離。
        hostileCount += CountHostiles(map);
        foodNutrition += map.resourceCounter.TotalHumanEdibleNutrition;
        fireCount += FireCount(map);
        accumulator.AccumulateColonists(map.mapPawns.FreeColonistsSpawned);
    }

    /// <summary>
    /// 構成實際威脅的敵對 pawn 數，沿用原版判定（攻擊目標快取＋IsActiveThreatTo）。
    /// 倒地、休眠中的機兵群、迷霧裡、逃跑中的都不算：留著會讓「襲擊結束」的邊緣永遠不歸零，
    /// 下一波真襲擊就觸發不了。守巢的蟲族照算（蟲災本身就是襲擊）。火與敵方砲塔也不算。
    /// </summary>
    public int GetHostileCount() => CountHostiles(Map);

    public IReadOnlyList<int> GetColonistsInMedicalEmergency()
    {
        var ids = new List<int>();
        foreach (Pawn pawn in Map.mapPawns.FreeColonistsSpawned)
        {
            // 與排程器訊號一樣逐個隔離：其他模組的 pawn 丟例外就跳過，不讓等待工具整個失敗。
            try
            {
                if (IsMedicalEmergency(pawn)) ids.Add(pawn.thingIDNumber);
            }
            catch (Exception ex)
            {
                WarnModError(pawn, "wait checks", ex);
            }
        }
        return ids;
    }

    public int GetFireCount() => FireCount(Map);

    private static int FireCount(Map map) => map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;

    /// <summary>流血到失血致死不到這麼久才算急診；小擦傷算出來要十幾天，不該打斷等待或起急診任務。</summary>
    private const int LifeThreateningBleedTicks = GenDate.TicksPerDay;

    /// <summary>
    /// 急診的判定，排程器訊號與 wait_for_time 的提早結束共用：倒地，或流血到一天內會失血致死。
    /// 麻醉中的不算：手術時病人本來就會倒地流血，那是排好的醫療行為，不是意外。
    /// </summary>
    private static bool IsMedicalEmergency(Pawn pawn)
    {
        if (pawn.health?.hediffSet == null) return pawn.Downed;
        if (pawn.health.hediffSet.HasHediff(HediffDefOf.Anesthetic)) return false;
        return pawn.Downed || HealthUtility.TicksUntilDeathDueToBloodLoss(pawn) < LifeThreateningBleedTicks;
    }

    private static int CountHostiles(Map map)
    {
        int count = 0;
        foreach (IAttackTarget target in map.attackTargetsCache.TargetsHostileToColony)
        {
            if (target.Thing is not Pawn pawn) continue;
            // IsActiveThreatTo 會跑到模組 pawn 的 Lord／心智狀態；一個丟例外只跳過它，其餘照數。
            try
            {
                if (GenHostility.IsActiveThreatTo(target, Faction.OfPlayer, ignoreHives: false)) count++;
            }
            catch (Exception ex)
            {
                WarnModError(pawn, "threat status", ex);
            }
        }
        return count;
    }

    public ResourceSnapshot GetResources()
    {
        ResourceCounter counter = Map.resourceCounter;
        return new ResourceSnapshot
        {
            TotalHumanEdibleNutrition = counter.TotalHumanEdibleNutrition,
            Items = [.. counter.AllCountedAmounts
                .Where(pair => pair.Value > 0)
                .Select(pair => new ResourceInfo
                {
                    DefName = pair.Key.defName,
                    Label = pair.Key.label,
                    Count = pair.Value,
                    Category = pair.Key.FirstThingCategory?.label ?? pair.Key.category.ToString()
                })]
        };
    }

    // AlertsReadout 的活動清單在 1.6 為私有快取；只讀取遊戲已計算的結果，
    // 找不到欄位時安全地回傳空清單，避免版本差異中斷 agent。
    #pragma warning disable S3011 // RimWorld 私有欄位與方法存取為 Mod 運作必需
    private static readonly FieldInfo ActiveAlertsField =
        typeof(AlertsReadout).GetField("activeAlerts", BindingFlags.Instance | BindingFlags.NonPublic);

    #pragma warning restore S3011

    private static IEnumerable<Alert> ActiveAlerts()
    {
        if (ActiveAlertsField?.GetValue(Find.Alerts) is not IEnumerable<Alert> alerts) return [];
        return [.. alerts.Where(a => a != null && a.Active)];
    }

    public IReadOnlyList<AlertInfo> GetAlerts()
    {
        return [.. ActiveAlerts().Select(a => new AlertInfo
        {
            Label = a.Label,
            Priority = a.Priority.ToString()
        })];
    }

    public IReadOnlyList<LetterInfo> GetRecentLetters()
    {
        return [.. Find.LetterStack.LettersListForReading
            .Where(l => l != null)
            .Take(20)
            .Select(l => new LetterInfo { Label = l.Label.ToString() })];
    }

    public PowerInfo GetPower()
    {
        List<PowerNet> nets = [.. Map.powerNetManager.AllNetsListForReading];
        return new PowerInfo
        {
            NetworkCount = nets.Count,
            NetWatts = nets.Sum(n => n.CurrentEnergyGainRate()),
            StoredEnergy = nets.Sum(n => n.CurrentStoredEnergy())
        };
    }

    public IReadOnlyList<ThreatInfo> GetThreats()
    {
        Map map = Map;
        var threats = new List<ThreatInfo>();
        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (pawn.Dead || !pawn.HostileTo(Faction.OfPlayer) || pawn.Fogged()) continue;
            string kind = ResolveThreatKind(pawn);
            threats.Add(new ThreatInfo
            {
                Id = pawn.thingIDNumber,
                Label = pawn.LabelShort,
                Kind = kind,
                Faction = pawn.Faction?.Name ?? string.Empty,
                X = pawn.Position.x,
                Z = pawn.Position.z,
                Downed = pawn.Downed
            });
        }
        foreach (Thing fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
        {
            if (fire.Fogged()) continue;
            threats.Add(new ThreatInfo { Id = fire.thingIDNumber, Label = fire.Label, Kind = "Fire", X = fire.Position.x, Z = fire.Position.z });
        }
        return threats;
    }

    /// <summary>
    /// 依 RaceProps 分類，不以「不是類人就是動物」推定：機兵、Anomaly 實體與模組的機器人／異種族都不是動物。
    /// 不用 RaceProps.Animal（要求血肉之軀），模組的非血肉野獸也照動物報。
    /// </summary>
    private static string ResolveThreatKind(Pawn pawn)
    {
        RaceProperties race = pawn.RaceProps;
        if (race.IsMechanoid) return "Mechanoid";
        if (race.IsAnomalyEntity) return "Entity";
        if (race.ToolUser) return "HostilePawn";
        return pawn.InMentalState ? "Manhunter" : "HostileAnimal";
    }
}

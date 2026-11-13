using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class AutonomousSchedulerTests
{
    private AutonomousScheduler _scheduler;
    private AutonomousConfig _config;

    [SetUp]
    public void SetUp()
    {
        _scheduler = new AutonomousScheduler();
        _config = new AutonomousConfig
        {
            Enabled = true,
            IntervalTicks = 15000,
            WindowTicks = 60000,
            CooldownTicks = AutonomousScheduler.CooldownTicks,
            OnRaid = true,
            OnMentalBreak = true,
            OnFoodShortage = true
        };
    }

    private static AutonomousSignals Calm(int colonists = 3)
    {
        return new AutonomousSignals { HostileCount = 0, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = colonists };
    }

    /// <summary>通電＋研究中：隔離火災訊號，不被停電／停研條件搶先。</summary>
    private static AutonomousSignals ActiveBase()
    {
        return new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, HasActiveResearch = true, AnyWorkingPower = true };
    }

    private static AutonomousSignals ActiveFire()
    {
        AutonomousSignals signals = ActiveBase();
        signals.FireCount = 1;
        return signals;
    }

    [Test]
    public void DisabledNeverFires()
    {
        _config.Enabled = false;
        var raid = new AutonomousSignals { HostileCount = 5, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(20000, raid, _config), Is.EqualTo(AutonomousTrigger.None));
    }

    [Test]
    public void IntervalFiresAfterIntervalElapses()
    {
        Assert.That(_scheduler.Evaluate(0, Calm(), _config), Is.EqualTo(AutonomousTrigger.None));
        Assert.That(_scheduler.Evaluate(15000, Calm(), _config), Is.EqualTo(AutonomousTrigger.Interval));
    }

    [Test]
    public void RaidFiresOnRisingEdgeAndRetriesWhileItLasts()
    {
        var raid = new AutonomousSignals { HostileCount = 3, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(0, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));
        // 敵人還在：退避沒到不重複開火。
        Assert.That(_scheduler.Evaluate(500, raid, _config), Is.EqualTo(AutonomousTrigger.None));
        // 退避到就再問一次——舊版在這裡就放棄了。
        Assert.That(_scheduler.Evaluate(2500, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));
    }

    [Test]
    public void UnresolvedConditionRetriesThenGivesUpOnce()
    {
        _config.OnFire = true;
        var fire = new AutonomousSignals { HostileCount = 0, FoodNutrition = 100f, ColonistCount = 3, FireCount = 1 };
        Assert.That(_scheduler.Evaluate(0, fire, _config), Is.EqualTo(AutonomousTrigger.Fire));
        Assert.That(_scheduler.Evaluate(1000, fire, _config), Is.EqualTo(AutonomousTrigger.None), "退避未到");
        Assert.That(_scheduler.Evaluate(2500, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "第一次重試");
        Assert.That(_scheduler.Evaluate(3000, fire, _config), Is.EqualTo(AutonomousTrigger.None), "退避未到");
        Assert.That(_scheduler.Evaluate(7500, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "第二次重試（退避加倍）");
        Assert.That(_scheduler.Evaluate(7500, fire, _config), Is.EqualTo(AutonomousTrigger.None));

        IReadOnlyList<AutonomousTrigger> giveUps = _scheduler.TakeGiveUps();
        Assert.That(giveUps, Is.EqualTo([AutonomousTrigger.Fire]), "用完次數要回報，讓玩家知道 agent 放棄了");
        Assert.That(_scheduler.TakeGiveUps(), Is.Empty, "同一次放棄只回報一次");
        // 定期巡檢照常觸發（那是設計裡的保底），但火不再被重試。
        Assert.That(_scheduler.Evaluate(100000, fire, _config), Is.Not.EqualTo(AutonomousTrigger.Fire), "放棄後不再重試");
    }

    [Test]
    public void AttemptOfCountsEveryTryAndResetsWhenTheConditionClears()
    {
        _config.OnFire = true;
        var fire = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, FireCount = 1 };
        var calm = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.AttemptOf(AutonomousTrigger.Fire), Is.Zero, "還沒觸發過");

        _scheduler.Evaluate(0, fire, _config);
        Assert.That(_scheduler.AttemptOf(AutonomousTrigger.Fire), Is.EqualTo(1));
        _scheduler.Evaluate(1000, fire, _config);
        Assert.That(_scheduler.AttemptOf(AutonomousTrigger.Fire), Is.EqualTo(1), "退避未到不算一次");
        _scheduler.Evaluate(2500, fire, _config);
        Assert.That(_scheduler.AttemptOf(AutonomousTrigger.Fire), Is.EqualTo(2));
        _scheduler.Evaluate(7500, fire, _config);
        Assert.That(_scheduler.AttemptOf(AutonomousTrigger.Fire), Is.EqualTo(3));

        _scheduler.Evaluate(8000, calm, _config);
        Assert.That(_scheduler.AttemptOf(AutonomousTrigger.Fire), Is.Zero, "條件解除後重新計次");
        Assert.That(_scheduler.AttemptOf(AutonomousTrigger.Interval), Is.Zero, "定期巡視不走重試表");
    }

    [Test]
    public void RetryBackoffCountsFromWhenTheRunFinished()
    {
        _config.OnFire = true;
        _config.CooldownTicks = 0;
        var fire = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, FireCount = 1 };
        Assert.That(_scheduler.Evaluate(0, fire, _config), Is.EqualTo(AutonomousTrigger.Fire));
        // 任務跑了 10000 tick（Superfast 下 LLM 延遲＋任務內等待很常見）才結束。
        _scheduler.RecordRunFinished(AutonomousTrigger.Fire, 10000);

        Assert.That(_scheduler.Evaluate(10500, fire, _config), Is.EqualTo(AutonomousTrigger.None),
            "從觸發算早就過了 2500，但上一輪剛結束、效果還沒時間反映，不能立刻重試");
        Assert.That(_scheduler.Evaluate(12500, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "結束後滿一段退避才重試");
    }

    [Test]
    public void CooldownAndHeartbeatCountFromTheRecordedFinishTick()
    {
        // 呼叫端傳的是任務結束 tick：一輪從 0 跑到 20000 才結束。
        _scheduler.RecordRun(20000, 5, _config);
        var raid = new AutonomousSignals { HostileCount = 3, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(21000, raid, _config), Is.EqualTo(AutonomousTrigger.None), "結束後的冷卻還沒過");
        Assert.That(_scheduler.Evaluate(22500, raid, _config), Is.EqualTo(AutonomousTrigger.Raid), "冷卻從結束起算");

        var other = new AutonomousScheduler();
        other.RecordRun(20000, 5, _config);
        Assert.That(other.Evaluate(30000, Calm(), _config), Is.EqualTo(AutonomousTrigger.None),
            "從起跑算早就超過 6 小時，但上一輪剛結束，不能馬上接一輪巡視");
        Assert.That(other.Evaluate(35000, Calm(), _config), Is.EqualTo(AutonomousTrigger.Interval), "結束後滿一個間隔才巡視");
    }

    [Test]
    public void RecordRunFinishedIgnoresTriggersOutsideTheRetryTable()
    {
        _config.OnFire = true;
        var fire = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, FireCount = 1 };
        _scheduler.RecordRunFinished(AutonomousTrigger.Fire, 5000);
        Assert.That(_scheduler.Evaluate(5500, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "沒觸發過的條件不受影響");
        Assert.DoesNotThrow(() => _scheduler.RecordRunFinished(AutonomousTrigger.Interval, 6000));
        Assert.DoesNotThrow(() => _scheduler.RecordRunFinished(AutonomousTrigger.None, 6000));
    }

    [Test]
    public void ClearingTheConditionRearmsForTheNextOccurrence()
    {
        _config.OnFire = true;
        var fire = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, FireCount = 1 };
        var calm = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(0, fire, _config), Is.EqualTo(AutonomousTrigger.Fire));
        for (int t = 1000; t <= 9000; t += 500) _scheduler.Evaluate(t, fire, _config);
        Assert.That(_scheduler.TakeGiveUps(), Is.Not.Empty, "先耗盡次數");

        Assert.That(_scheduler.Evaluate(9500, calm, _config), Is.EqualTo(AutonomousTrigger.None), "火滅了");
        Assert.That(_scheduler.Evaluate(10000, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "重新起火要能再觸發");
    }

    [Test]
    public void FoodWarningTierFiresBeforeStarvation()
    {
        // 人均 10：警戒線 8 以下才算偏緊，10 不算。
        Assert.That(_scheduler.Evaluate(0, Calm(), _config), Is.EqualTo(AutonomousTrigger.None));
        var tight = new AutonomousSignals { FoodNutrition = 3 * 7f, ColonistCount = 3, HasActiveResearch = true, AnyWorkingPower = true };
        Assert.That(_scheduler.Evaluate(500, tight, _config), Is.EqualTo(AutonomousTrigger.FoodShortage), "偏緊就叫醒，不等餓死");
        // 繼續惡化到危急要再叫一次。
        var starving = new AutonomousSignals { FoodNutrition = 3 * 2f, ColonistCount = 3, HasActiveResearch = true, AnyWorkingPower = true };
        Assert.That(_scheduler.Evaluate(1000, starving, _config), Is.EqualTo(AutonomousTrigger.FoodShortage), "分級變差要立刻再叫");
        // 補回來了就恢復正常，下次再缺才重新算。
        Assert.That(_scheduler.Evaluate(1500, Calm(), _config), Is.EqualTo(AutonomousTrigger.None));
        Assert.That(_scheduler.Evaluate(2000, tight, _config), Is.EqualTo(AutonomousTrigger.FoodShortage));
    }

    [Test]
    public void UpkeepConditionsFireAndShareOneSwitch()
    {
        _config.OnUpkeep = true;
        _config.OnFire = true;
        _config.OnEmergencyMedical = true;
        var cases = new[]
        {
            Tuple.Create(new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, ColdColonistCount = 1 }, AutonomousTrigger.Cold),
            Tuple.Create(new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, BedlessCount = 2 }, AutonomousTrigger.BedShortage),
            Tuple.Create(new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, OutdoorsBedCount = 1 }, AutonomousTrigger.OutdoorsBed),
            // 停電與停研互斥不了：沒電就會停研，先給它通電，兩者各測一次。
            Tuple.Create(new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, HasActiveResearch = true }, AutonomousTrigger.PowerOutage),
            Tuple.Create(new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, AnyWorkingPower = true }, AutonomousTrigger.Research),
            Tuple.Create(new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, AnyWorkingPower = true, HasActiveResearch = true, LowMoodCount = 2 }, AutonomousTrigger.LowMood)
        };
        foreach (var c in cases)
        {
            _scheduler.Reset();
            Assert.That(_scheduler.Evaluate(0, c.Item1, _config), Is.EqualTo(c.Item2));
        }
    }

    [Test]
    public void UpkeepSwitchOffSilencesBaseConditions()
    {
        _config.OnUpkeep = false;
        // 沒有通電、沒在研究、又受寒又沒床：全都不該叫。
        var bad = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, ColdColonistCount = 2, BedlessCount = 2 };
        Assert.That(_scheduler.Evaluate(0, bad, _config), Is.EqualTo(AutonomousTrigger.None));
        Assert.That(_scheduler.Evaluate(20000, bad, _config), Is.EqualTo(AutonomousTrigger.Interval), "只剩定期巡視");
    }

    /// <summary>
    /// 開關關掉時要清空重試狀態。否則玩家把開關關掉再開、條件又還沒恢復時，
    /// 會直接吃到「次數用完」的舊狀態，之後再也沒人處理也沒有第二次回報。
    /// </summary>
    [Test]
    public void TurningASwitchOffThenOnStartsAFreshCount()
    {
        _config.OnUpkeep = true;
        var cold = new AutonomousSignals
        {
            FoodNutrition = 100f, ColonistCount = 3,
            AnyWorkingPower = true, HasActiveResearch = true, ColdColonistCount = 1
        };
        // 耗盡三次並放棄。
        Assert.That(_scheduler.Evaluate(0, cold, _config), Is.EqualTo(AutonomousTrigger.Cold));
        Assert.That(_scheduler.Evaluate(2500, cold, _config), Is.EqualTo(AutonomousTrigger.Cold));
        Assert.That(_scheduler.Evaluate(7500, cold, _config), Is.EqualTo(AutonomousTrigger.Cold));
        _scheduler.Evaluate(8000, cold, _config);
        Assert.That(_scheduler.TakeGiveUps(), Is.EqualTo([AutonomousTrigger.Cold]));

        // 玩家關掉再開；受寒還沒解決。
        _config.OnUpkeep = false;
        _scheduler.Evaluate(9000, cold, _config);
        _config.OnUpkeep = true;
        _scheduler.Evaluate(10000, cold, _config);

        Assert.That(_scheduler.Evaluate(20000, cold, _config), Is.EqualTo(AutonomousTrigger.Cold),
            "重新開啟後要從頭算次數");
    }

    [Test]
    public void TurningRaidOffThenOnForgetsTheOldBackoff()
    {
        var raid = new AutonomousSignals { HostileCount = 2, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(0, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));

        _config.OnRaid = false;
        _scheduler.Evaluate(1000, raid, _config);
        _config.OnRaid = true;

        Assert.That(_scheduler.Evaluate(1100, raid, _config), Is.Not.EqualTo(AutonomousTrigger.None),
            "重開後的襲擊要能再觸發，不吃舊的退避");
    }

    [Test]
    public void HeartbeatIsAFixedConstantNotPlayerConfigurable()
    {
        Assert.That(AutonomousScheduler.HeartbeatTicks, Is.EqualTo(15000));
    }

    [Test]
    public void RetryStateSurvivesSaveAndReload()
    {
        _config.OnFire = true;
        var fire = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, FireCount = 1 };
        Assert.That(_scheduler.Evaluate(0, fire, _config), Is.EqualTo(AutonomousTrigger.Fire));
        Assert.That(_scheduler.Evaluate(2500, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "已用掉兩次");

        // 走一次存檔／讀檔的純邏輯（實際 Scribe 由 GameComponent 串接）。
        IReadOnlyList<AutonomousWatch> saved = _scheduler.CaptureWatch();
        Assert.That(saved, Has.Count.EqualTo(1), "只用過一個條件");
        var reloaded = new AutonomousScheduler();
        reloaded.ApplyWatch(saved);

        // 讀檔後不該從零開始算次數：兩次的用量得接續，第三次一樣會放棄。
        Assert.That(reloaded.Evaluate(2600, fire, _config), Is.EqualTo(AutonomousTrigger.None), "退避未到");
        Assert.That(reloaded.Evaluate(7500, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "最後一次嘗試");
        Assert.That(reloaded.Evaluate(9000, fire, _config), Is.EqualTo(AutonomousTrigger.None));
        Assert.That(reloaded.TakeGiveUps(), Is.EqualTo([AutonomousTrigger.Fire]));
    }

    [Test]
    public void ApplyWatchMustRunAfterCopyTo()
    {
        // CopyTo 會整片覆蓋 _watch：呼叫端若照相反順序套用，讀檔後重試狀態會被清掉。
        _config.OnFire = true;
        var fire = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, FireCount = 1 };
        _scheduler.Evaluate(0, fire, _config);
        _scheduler.Evaluate(2500, fire, _config);
        IReadOnlyList<AutonomousWatch> saved = _scheduler.CaptureWatch();

        // 模擬 GameComponent.LoadedGame 的順序。
        var reloaded = new AutonomousScheduler();
        var loaded = new AutonomousScheduler();
        loaded.CopyTo(reloaded);
        reloaded.ApplyWatch(saved);

        Assert.That(reloaded.Evaluate(7500, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "接續第三次嘗試");
    }

    [Test]
    public void OldSaveWithoutWatchTableStartsFresh()
    {
        _config.OnFire = true;
        var fire = new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 3, FireCount = 1 };
        var reloaded = new AutonomousScheduler();
        reloaded.ApplyWatch(new List<AutonomousWatch>());

        Assert.That(reloaded.Evaluate(20000, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "舊存檔照常能觸發");
    }

    /// <summary>
    /// 玩家按停止中止的自主任務：條件解除前不再重試，也不發放棄通知。
    /// 之前只取消 token，退避一到排程器就自己跑回來，等於停止鍵沒用。
    /// </summary>
    [Test]
    public void DismissingARunStopsRetriesUntilTheConditionClears()
    {
        _config.OnFire = true;
        AutonomousSignals fire = ActiveFire();
        AutonomousSignals calm = ActiveBase();
        Assert.That(_scheduler.Evaluate(0, fire, _config), Is.EqualTo(AutonomousTrigger.Fire));

        _scheduler.Dismiss(AutonomousTrigger.Fire, 0);

        Assert.That(_scheduler.Evaluate(2500, fire, _config), Is.EqualTo(AutonomousTrigger.None), "退避到了也不重試");
        Assert.That(_scheduler.Evaluate(20000, fire, _config), Is.Not.EqualTo(AutonomousTrigger.Fire), "之後也不重試（定期巡視除外）");
        Assert.That(_scheduler.TakeGiveUps(), Is.Empty, "玩家自己按的停止不發放棄通知");

        Assert.That(_scheduler.Evaluate(20500, calm, _config), Is.EqualTo(AutonomousTrigger.Interval), "無事時只剩定期巡視");
        Assert.That(_scheduler.Evaluate(21000, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "條件解除後重新計次");
    }

    [Test]
    public void DismissedConditionStillFiresWhenItGetsWorse()
    {
        var tight = new AutonomousSignals { FoodNutrition = 3 * 7f, ColonistCount = 3, HasActiveResearch = true, AnyWorkingPower = true };
        var starving = new AutonomousSignals { FoodNutrition = 3 * 2f, ColonistCount = 3, HasActiveResearch = true, AnyWorkingPower = true };
        Assert.That(_scheduler.Evaluate(0, tight, _config), Is.EqualTo(AutonomousTrigger.FoodShortage));

        _scheduler.Dismiss(AutonomousTrigger.FoodShortage, 0);

        Assert.That(_scheduler.Evaluate(2500, tight, _config), Is.EqualTo(AutonomousTrigger.None), "層級持平不重試");
        Assert.That(_scheduler.Evaluate(3000, starving, _config), Is.EqualTo(AutonomousTrigger.FoodShortage), "惡化要立刻再叫");
        Assert.That(_scheduler.TakeGiveUps(), Is.Empty);
    }

    [Test]
    public void DismissingIdleOrEmptyTriggerDoesNothing()
    {
        _scheduler.Dismiss(AutonomousTrigger.None, 0);
        _config.OnFire = true;
        AutonomousSignals fire = ActiveFire();
        _scheduler.Dismiss(AutonomousTrigger.Fire, 0);
        Assert.That(_scheduler.Evaluate(0, fire, _config), Is.EqualTo(AutonomousTrigger.Fire), "沒跑過的條件不受影響");
    }

    /// <summary>
    /// 中止一次定期巡視：下一次巡視是一個完整間隔之後，不是下次檢查就回來。
    /// 定期巡視不經重試表，壓制靠心跳時鐘。
    /// </summary>
    [Test]
    public void DismissingAnIntervalRunDelaysTheNextHeartbeat()
    {
        Assert.That(_scheduler.Evaluate(15000, Calm(), _config), Is.EqualTo(AutonomousTrigger.Interval));

        _scheduler.Dismiss(AutonomousTrigger.Interval, 15000);

        Assert.That(_scheduler.Evaluate(15500, Calm(), _config), Is.EqualTo(AutonomousTrigger.None));
        Assert.That(_scheduler.Evaluate(29999, Calm(), _config), Is.EqualTo(AutonomousTrigger.None));
        Assert.That(_scheduler.Evaluate(30000, Calm(), _config), Is.EqualTo(AutonomousTrigger.Interval), "完整間隔後照常巡視");
        Assert.That(_scheduler.TakeGiveUps(), Is.Empty);
    }

    [Test]
    public void CooldownSuppressesImmediateRefire()
    {
        _scheduler.RecordRun(0, 2, _config);
        var raid = new AutonomousSignals { HostileCount = 3, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(500, raid, _config), Is.EqualTo(AutonomousTrigger.None));
        Assert.That(_scheduler.Evaluate(2500, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));
    }

    [Test]
    public void CooldownDisabledAllowsImmediateRefire()
    {
        _config.CooldownTicks = 0; // 關閉冷卻
        _scheduler.RecordRun(0, 2, _config);
        var raid = new AutonomousSignals { HostileCount = 3, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(10, raid, _config), Is.EqualTo(AutonomousTrigger.Raid), "冷卻設為 0 時立即反應");
    }

    [Test]
    public void HeavyToolUseNeverBlocksTriggers()
    {
        _scheduler.RecordRun(0, 100, _config);
        var raid = new AutonomousSignals { HostileCount = 3, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(2500, raid, _config), Is.EqualTo(AutonomousTrigger.Raid), "自主模式不設每日預算上限");
    }

    [Test]
    public void SimultaneousEventsFireOneAfterAnother()
    {
        _config.OnFire = true;
        var raidAndFire = new AutonomousSignals { HostileCount = 3, FoodNutrition = 100f, ColonistCount = 3, FireCount = 2 };
        Assert.That(_scheduler.Evaluate(0, raidAndFire, _config), Is.EqualTo(AutonomousTrigger.Raid));
        // 火災與襲擊同一次檢查出現：沒輪到的火災留到下次，不被吃掉。
        Assert.That(_scheduler.Evaluate(500, raidAndFire, _config), Is.EqualTo(AutonomousTrigger.Fire));
        Assert.That(_scheduler.Evaluate(1000, raidAndFire, _config), Is.EqualTo(AutonomousTrigger.None));
    }

    [Test]
    public void MentalBreakAndFoodShortageFireOnEdge()
    {
        var mental = new AutonomousSignals { HostileCount = 0, NearMentalBreakCount = 1, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(0, mental, _config), Is.EqualTo(AutonomousTrigger.MentalBreak));

        _scheduler.Reset();
        var hungry = new AutonomousSignals { HostileCount = 0, NearMentalBreakCount = 0, FoodNutrition = 5f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(0, hungry, _config), Is.EqualTo(AutonomousTrigger.FoodShortage));
    }

    [Test]
    public void NoColonistsMeansNoFoodShortage()
    {
        var empty = new AutonomousSignals { HostileCount = 0, NearMentalBreakCount = 0, FoodNutrition = 0f, ColonistCount = 0 };
        Assert.That(_scheduler.Evaluate(20000, empty, _config), Is.EqualTo(AutonomousTrigger.Interval));
    }

    [Test]
    public void RaidBeatsIntervalInPriority()
    {
        var raid = new AutonomousSignals { HostileCount = 2, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(20000, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));
    }

    [Test]
    public void ToolsUsedResetsWhenWindowRolls()
    {
        _scheduler.RecordRun(0, 60, _config);
        Assert.That(_scheduler.ToolsUsed, Is.EqualTo(60));

        // 視窗過了就重置當日用量。
        _scheduler.Evaluate(61000, Calm(), _config);
        Assert.That(_scheduler.ToolsUsed, Is.EqualTo(0));
    }

    [Test]
    public void DisabledTriggerTypesAreIgnored()
    {
        _config.OnRaid = false;
        _config.OnMentalBreak = false;
        _config.OnFoodShortage = false;
        var bad = new AutonomousSignals { HostileCount = 5, NearMentalBreakCount = 2, FoodNutrition = 0f, ColonistCount = 3 };
        Assert.That(_scheduler.Evaluate(0, bad, _config), Is.EqualTo(AutonomousTrigger.None));
    }
}

[TestFixture]
public class AutonomousPromptsTests
{
    private static AutonomousPack Pack()
    {
        return new AutonomousPack
        {
            Tasks = new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                { "Raid", "Hostiles are on the map." },
                { "FoodShortage", "Food reserves are low." },
                { "MentalBreak", "A colonist is close to a mental break." },
                { "Interval", "Routine inspection task." }
            }
        };
    }

    [Test]
    public void PromptsMentionTheTrigger()
    {
        Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.Raid), Does.Contain("Hostiles"));
        Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.FoodShortage), Does.Contain("Food reserves"));
        Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.MentalBreak), Does.Contain("mental break"));
        Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.Interval), Does.Contain("Routine inspection"));
    }

    [Test]
    public void UnknownTriggerFallsBackToInterval()
    {
        Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.Fire), Does.Contain("Routine inspection"));
    }

    [Test]
    public void MissingTaskReturnsNullSoTheRunIsSkipped()
    {
        Assert.That(AutonomousPrompts.Build(null, AutonomousTrigger.Raid), Is.Null);
        Assert.That(AutonomousPrompts.Build(new AutonomousPack(), AutonomousTrigger.Raid), Is.Null);
    }

    [Test]
    public void HasTaskReportsSectionPresence()
    {
        Assert.That(AutonomousPrompts.HasTask(Pack(), AutonomousTrigger.Raid), Is.True);
        Assert.That(AutonomousPrompts.HasTask(Pack(), AutonomousTrigger.Fire), Is.False, "退回 Interval 的情況呼叫端要能察覺");
        Assert.That(AutonomousPrompts.HasTask(null, AutonomousTrigger.Raid), Is.False);
    }

    [Test]
    public void AutonomousPromptContainsOnlyItsOwnTask()
    {
        string text = AutonomousPrompts.Build(Pack(), AutonomousTrigger.Interval);
        Assert.That(text, Does.Not.Contain("goals:"));
    }
}

[TestFixture]
public class AutonomousToolsTests
{
    private static readonly string[] HighRiskTools =
    [
        "schedule_surgery",
        "set_prisoner_interaction",
        "execute_trade",
        "form_caravan",
        "send_caravan",
        "settle_caravan",
        "release_prisoner"
    ];

    [Test]
    public void AutonomousModeIncludesAllToolsWithoutBlacklist()
    {
        var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript());
        var tools = registry.Select(ToolPermissions.All).OfType<AIFunction>().Select(f => f.Name).ToList();

        foreach (string name in HighRiskTools)
        {
            Assert.That(tools, Does.Contain(name), name);
        }
        Assert.That(tools, Does.Contain("set_work_priorities"));
    }
}

[TestFixture]
public class AutonomousRetryDetailTests
{
    [Test]
    public void InitialAttemptReturnsNull()
    {
        var signals = new AutonomousSignals { FireCount = 2, ColdColonistCount = 1 };
        Assert.That(RimAgentGameComponent.FormatRetryDetail(AutonomousTrigger.Fire, 1, signals), Is.Null);
        Assert.That(RimAgentGameComponent.FormatRetryDetail(AutonomousTrigger.Cold, 0, signals), Is.Null);
    }

    [Test]
    public void RetryAttemptsIncludeSpecificDiagnostics()
    {
        var signals = new AutonomousSignals
        {
            FireCount = 3,
            ColdColonistCount = 2,
            DownedOrBleedingCount = 1,
            FoodNutrition = 12f,
            ColonistCount = 3
        };

        string fireDetail = RimAgentGameComponent.FormatRetryDetail(AutonomousTrigger.Fire, 2, signals);
        Assert.That(fireDetail, Does.Contain("retry #2").And.Contain("3 fire(s) burning"));

        string coldDetail = RimAgentGameComponent.FormatRetryDetail(AutonomousTrigger.Cold, 3, signals);
        Assert.That(coldDetail, Does.Contain("retry #3").And.Contain("2 colonist(s) remain dangerously cold"));

        string medDetail = RimAgentGameComponent.FormatRetryDetail(AutonomousTrigger.EmergencyMedical, 2, signals);
        Assert.That(medDetail, Does.Contain("retry #2").And.Contain("1 colonist(s) remain downed or bleeding"));

        string foodDetail = RimAgentGameComponent.FormatRetryDetail(AutonomousTrigger.FoodShortage, 2, signals);
        Assert.That(foodDetail, Does.Contain("retry #2").And.Contain("4.0 nutrition"));

        string fallbackDetail = RimAgentGameComponent.FormatRetryDetail(AutonomousTrigger.Research, 2, signals);
        Assert.That(fallbackDetail, Does.Contain("retry #2").And.Contain("condition: Research."));
    }
}

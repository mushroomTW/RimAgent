using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests
{
    /// <summary>套用本測試 mod 的 Harmony 補丁（只有 <see cref="TestDriver"/>）。</summary>
    public sealed class InGameTestsMod : Mod
    {
        public InGameTestsMod(ModContentPack content) : base(content)
        {
            new Harmony("GreenMushroom.RimAgent.InGameTests").PatchAll(typeof(InGameTestsMod).Assembly);
        }
    }

    /// <summary>
    /// 取代 RimTest Redux 內建的「啟動時執行」（seed_config 已關掉）：內建版只在第一次閒置時跑一輪，
    /// 用 -quicktest 時那一刻地圖可能還沒穩定。這裡分兩種輪次——
    /// 主選單閒置時跑一輪（地圖測試記 skipped），每進入一場有當前地圖的遊戲、等畫面穩定後再跑一輪。
    /// RimAgent 啟動期沒有背景工作（Task 只在玩家下指令、自主排程或 MCP 暖機時才起），不需要額外的完成訊號。
    /// </summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class TestDriver
    {
        /// <summary>進入遊戲後等這麼多幀再跑，讓首幀的 UI、警示與地圖快取先建好。</summary>
        private const int SettleFrames = 120;

        // RimTest Redux 的 internal 方法延遲解析：解析不到只記一次錯誤並停用，不讓每幀的 Update 都丟例外。
        private static Action _runAll;
        private static Action _updateCounts;
        private static Action _logResults;
        private static bool _disabled;

        private static bool _menuRoundDone;
        // 只記遊戲的識別碼，不持有 Game 本身：離開遊戲後整張地圖才能被回收。
        private static int? _testedGame;
        private static int? _pendingGame;
        private static int _pendingSince;

        public static void Postfix()
        {
            if (_disabled || !PlayDataLoader.Loaded || LongEventHandler.AnyEventNowOrWaiting || Find.UIRoot == null) return;

            if (Current.ProgramState == ProgramState.Entry)
            {
                if (_menuRoundDone) return;
                _menuRoundDone = true;
                Run("main menu");
                return;
            }

            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            int game = RuntimeHelpers.GetHashCode(Current.Game);
            if (game == _testedGame) return;
            if (game != _pendingGame)
            {
                _pendingGame = game;
                _pendingSince = Time.frameCount;
                return;
            }
            if (Time.frameCount - _pendingSince < SettleFrames) return;
            _testedGame = game;
            Run("map");
        }

        private static void Run(string round)
        {
            if (!Resolve()) return;
            Log.Message(TestHelpers.LogPrefix + "round start: " + round);
            _runAll();
            _updateCounts();
            _logResults();
            // 載入期檢查的結果之後不會變，之後的輪次只重跑需要地圖的測試。
            TestHelpers.StartupSuiteDone = true;
            Log.Message(TestHelpers.LogPrefix + "round end: " + round);
        }

        private static bool Resolve()
        {
            if (_runAll != null) return true;
            try
            {
                _runAll = Internal("Runner:RunAllRegisteredTests");
                _updateCounts = Internal("StatusExplorer:UpdateAllStatusCounts");
                _logResults = Internal("Viewer:LogTestsResults");
                return true;
            }
            catch (Exception ex)
            {
                _runAll = null;
                _disabled = true;
                Log.Error(TestHelpers.LogPrefix + "RimTest Redux internals changed; test driver disabled: " + ex.Message);
                return false;
            }
        }

        private static Action Internal(string typeColonMethod)
        {
            var method = AccessTools.Method("RimTestRedux.Testing." + typeColonMethod);
            if (method == null) throw new MissingMethodException("RimTestRedux.Testing." + typeColonMethod);
            return AccessTools.MethodDelegate<Action>(method);
        }
    }
}

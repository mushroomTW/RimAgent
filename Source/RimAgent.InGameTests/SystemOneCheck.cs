using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimAgent.Agent;
using RimAgent.Game;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// System One 代答抉擇與篩選基地狀況的遊戲端流程：用假的決策模型（答案在背景執行緒回來），
/// 確認真的對話框被按下、按鈕在主線程執行、對話紀錄與回報有寫，篩選會延後而不叫主 LLM。不需要模型與金鑰。
/// </summary>
internal static class SystemOneCheck
{
    private sealed class StubDecisionService : IDecisionService
    {
        private readonly Func<DecisionPlan, DecisionResult> _reply;
        public int Calls;

        public StubDecisionService(Func<DecisionPlan, DecisionResult> reply)
        {
            _reply = reply;
        }

        public async Task<DecisionResult> EvaluateAsync(string state, DecisionPlan plan, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            // 真模型的回應來自背景執行緒：呼叫端之後碰遊戲物件必須已回到主線程。
            await Task.Delay(30).ConfigureAwait(false);
            return _reply(plan);
        }
    }

    private static readonly Type Comp = typeof(RimAgentGameComponent);

    private static T Field<T>(object target, string name) => (T)AccessTools.Field(Comp, name).GetValue(target);

    private static Task<T> Call<T>(object target, string name, params object[] args)
    {
        return (Task<T>)AccessTools.Method(Comp, name).Invoke(target, args);
    }

    private static int Notices(ColonyAgentSession session, string text)
    {
        return session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Notice && e.Text.Contains(text));
    }

    public static async Task<List<string>> RunAsync()
    {
        var failures = new List<string>();
        RimAgentGameComponent comp = RimAgentGameComponent.Instance;
        RimAgentSettings settings = RimAgentMod.Settings;
        ColonyAgentSession session = comp.Session;
        // 背景照顧關掉，避免遊戲自己的檢查同時接走測試對話框。
        settings.autonomousCare = false;
        settings.decisionAnswerChoices = true;
        settings.decisionTriageUpkeep = true;
        settings.confirmAutonomousMajor = true;

        await CheckChoiceAsync(comp, settings, session, failures);
        await CheckTriageAsync(comp, session, failures);
        return failures;
    }

    private static async Task CheckChoiceAsync(RimAgentGameComponent comp, RimAgentSettings settings, ColonyAgentSession session, List<string> failures)
    {
        string pressed = null;
        bool onMainThread = false;
        var dialog = new Dialog_MessageBox("A wanderer asks to join.", "Accept", () =>
        {
            pressed = "Accept";
            onMainThread = UnityData.IsInMainThread;
        }, "Reject", () => pressed = "Reject", "System One check");
        Find.WindowStack.Add(dialog);
        var stub = new StubDecisionService(plan => new DecisionResult(plan.Questions.ToDictionary(
            q => q.Id,
            q => new DecisionAnswer { Value = q.Options.First(o => o.Value == "Accept").Key, Confidence = 0.9 })));
        AccessTools.Field(Comp, "_choiceResponder").SetValue(comp, new DecisionChoiceResponder(stub, () => "Pick what helps the colony."));
        try
        {
            List<PendingChoiceInfo> choices = [.. new RimWorldGameWorld(Current.Game).GetPendingChoices().Where(c => c.Title == "System One check" || c.Text.Contains("wanderer asks"))];
            if (choices.Count != 1)
            {
                failures.Add($"test dialog not pending exactly once ({choices.Count})");
                return;
            }
            int generation = Field<int>(comp, "_autoGeneration");

            // 確認模式不代答，也不問決策模型。
            settings.mode = AgentMode.Confirm;
            HashSet<string> none = await Call<HashSet<string>>(comp, "AnswerChoicesWithSystemOneAsync", session, choices, generation);
            if (none.Count != 0 || stub.Calls != 0 || pressed != null) failures.Add($"confirm mode answered: {none.Count}, calls {stub.Calls}, pressed {pressed}");

            // 執行模式且要求自主重大動作確認：同樣不代答。
            settings.mode = AgentMode.Execute;
            none = await Call<HashSet<string>>(comp, "AnswerChoicesWithSystemOneAsync", session, choices, generation);
            if (none.Count != 0 || stub.Calls != 0) failures.Add("execute mode with confirmation answered");

            // 完整權限：走完整的抉擇任務流程；全答完就不叫主 LLM。
            settings.mode = AgentMode.Autonomous;
            int noticesBefore = Notices(session, "System One answered");
            object config = AccessTools.Method(Comp, "ToConfig").Invoke(comp, [settings]);
            AccessTools.Field(Comp, "_autoRunning").SetValue(comp, true);
            await (Task)AccessTools.Method(Comp, "RunChoiceGuardedAsync").Invoke(comp, [session, choices, Find.TickManager.TicksGame, config]);

            if (pressed != "Accept") failures.Add("dialog not answered with Accept, pressed " + (pressed ?? "nothing"));
            if (!onMainThread) failures.Add("dialog button ran off the main thread");
            if (Find.WindowStack.Windows.Contains(dialog)) failures.Add("dialog still open");
            if (stub.Calls != 1) failures.Add("decision model calls " + stub.Calls);
            if (Notices(session, "System One answered") != noticesBefore + 1) failures.Add("no transcript notice for the answer");
            string reports = session.DecisionReports.TakeAll();
            if (reports == null || !reports.Contains("Accept")) failures.Add("main model report missing: " + (reports ?? "null"));
            if (Field<bool>(comp, "_autoRunning")) failures.Add("_autoRunning still set");
            if (session.IsBusy) failures.Add("main model was started although every choice was answered");
        }
        finally
        {
            if (Find.WindowStack.Windows.Contains(dialog)) dialog.Close(false);
            AccessTools.Field(Comp, "_choiceResponder").SetValue(comp, null);
        }
    }

    private static async Task CheckTriageAsync(RimAgentGameComponent comp, ColonyAgentSession session, List<string> failures)
    {
        double need = 0.1;
        var stub = new StubDecisionService(plan => new DecisionResult(new Dictionary<string, DecisionAnswer>
        {
            [DecisionAutonomousTriage.QuestionId] = new DecisionAnswer { Probability = need }
        }));
        AccessTools.Field(Comp, "_triage").SetValue(comp, new DecisionAutonomousTriage(stub, () => "Skip when already handled."));
        try
        {
            var signals = new AutonomousSignals { ColonistCount = 3, BedlessCount = 1 };
            int tick = Find.TickManager.TicksGame;
            int generation = Field<int>(comp, "_autoGeneration");
            int before = Notices(session, "System One judged");

            bool skip = await Call<bool>(comp, "TriageSkipsAsync", session, AutonomousTrigger.BedShortage, signals, tick, generation);
            if (!skip) failures.Add("low need was not skipped");
            skip = await Call<bool>(comp, "TriageSkipsAsync", session, AutonomousTrigger.BedShortage, signals, tick, generation);
            if (!skip) failures.Add("second low need was not skipped");
            if (Notices(session, "System One judged") != before + 1) failures.Add("triage notice not shown exactly once per wait");

            need = 0.9;
            if (await Call<bool>(comp, "TriageSkipsAsync", session, AutonomousTrigger.BedShortage, signals, tick, generation)) failures.Add("high need was skipped");
            int calls = stub.Calls;
            if (await Call<bool>(comp, "TriageSkipsAsync", session, AutonomousTrigger.Fire, signals, tick, generation)) failures.Add("fire was skipped");
            if (stub.Calls != calls) failures.Add("fire was sent to the decision model");
            if (stub.Calls != 3) failures.Add("triage decision calls " + stub.Calls);
        }
        finally
        {
            AccessTools.Field(Comp, "_triage").SetValue(comp, null);
        }
    }
}

/// <summary>隔離環境 Config 有標記檔時，進圖後跑一次，結果以 "[RimAgent.InGameTests] SystemOneCheck PASS/FAIL" 警告輸出。</summary>
public sealed class SystemOneCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentSystemOneCheck.xml";
    private static bool _started;

    public SystemOneCheckComponent(Verse.Game game)
    {
    }

    public override void GameComponentUpdate()
    {
        if (_started || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        _started = true;
        if (!File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName))) return;
        _ = RunAsync();
    }

    private static async Task RunAsync()
    {
        string prefix = TestHelpers.LogPrefix + "SystemOneCheck ";
        try
        {
            List<string> failures = await SystemOneCheck.RunAsync();
            Log.Warning(prefix + (failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count}): " + string.Join(" | ", failures)));
        }
        catch (Exception ex)
        {
            Log.Warning(prefix + "FAIL exception: " + ex);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimAgent.Agent;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// System One 篩選遊戲警報的遊戲端流程：用假的決策模型（答案在背景執行緒回來），
/// 確認篩選會延後而不叫主 LLM，紅色警報與應急事件不送決策模型。不需要模型與金鑰。
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
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            return _reply(plan);
        }
    }

    private static readonly Type Comp = typeof(RimAgentGameComponent);
    private const string TriageSkipsAsync = "TriageSkipsAsync";

    private static T Field<T>(object target, string name) => (T)AccessTools.Field(Comp, name).GetValue(target);

    private static Task<T> Call<T>(object target, string name, params object[] args)
    {
        return (Task<T>)AccessTools.Method(Comp, name).Invoke(target, args);
    }

    private static int Notices(ColonyAgentSession session, string text)
    {
        return session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Notice && e.Text.Contains(text, StringComparison.Ordinal));
    }

    public static async Task<List<string>> RunAsync()
    {
        var failures = new List<string>();
        RimAgentGameComponent comp = RimAgentGameComponent.Instance;
        RimAgentSettings settings = RimAgentMod.Settings;
        ColonyAgentSession session = comp.Session;
        // 背景照顧關掉，避免遊戲自己的檢查同時起跑。
        settings.autonomousCare = false;
        settings.decisionTriageUpkeep = true;

        await CheckTriageAsync(comp, session, failures);
        return failures;
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
            var signals = new AutonomousSignals { ColonistCount = 3, AlertCount = 1, Alerts = "Need colonist beds" };
            int tick = Find.TickManager.TicksGame;
            int generation = Field<AutonomousRunner>(comp, "_runner").Generation;
            int before = Notices(session, "System One judged");

            bool skip = await Call<bool>(comp, TriageSkipsAsync, session, AutonomousTrigger.Alert, signals, tick, generation, CancellationToken.None);
            if (!skip) failures.Add("low need was not skipped");
            skip = await Call<bool>(comp, TriageSkipsAsync, session, AutonomousTrigger.Alert, signals, tick, generation, CancellationToken.None);
            if (!skip) failures.Add("second low need was not skipped");
            if (Notices(session, "System One judged") != before + 1) failures.Add("triage notice not shown exactly once per wait");

            need = 0.9;
            if (await Call<bool>(comp, TriageSkipsAsync, session, AutonomousTrigger.Alert, signals, tick, generation, CancellationToken.None)) failures.Add("high need was skipped");
            int calls = stub.Calls;
            if (await Call<bool>(comp, TriageSkipsAsync, session, AutonomousTrigger.Fire, signals, tick, generation, CancellationToken.None)) failures.Add("fire was skipped");
            if (await Call<bool>(comp, TriageSkipsAsync, session, AutonomousTrigger.CriticalAlert, signals, tick, generation, CancellationToken.None)) failures.Add("critical alert was skipped");
            if (stub.Calls != calls) failures.Add("fire or critical alert was sent to the decision model");
            if (stub.Calls != 3) failures.Add("triage decision calls " + stub.Calls.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            AccessTools.Field(Comp, "_triage").SetValue(comp, value: null);
        }
    }
}

/// <summary>隔離環境 Config 有標記檔時，進圖後跑一次，結果以 "[RimAgent.InGameTests] SystemOneCheck PASS/FAIL" 警告輸出。</summary>
public sealed class SystemOneCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentSystemOneCheck.xml";
    private bool _started;

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

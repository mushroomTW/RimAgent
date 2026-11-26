using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

public static class DecisionModelTester
{
    private const string QuestionId = "test";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static int _running;
    private static volatile TestOutcome _outcome = TestOutcome.None;
    private static volatile string _detail = string.Empty;

    public enum TestOutcome { None, Ok, Unavailable, NoAnswer, Failed }

    public static bool Running => Volatile.Read(ref _running) != 0;

    /// <summary>最近一次測試的結果；文字由設定頁翻譯（背景執行緒不碰翻譯）。</summary>
    public static TestOutcome Outcome => _outcome;

    public static string Detail => _detail;

    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _outcome = TestOutcome.None;
        _detail = string.Empty;
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        var timer = Stopwatch.StartNew();
        try
        {
            IDecisionService service = DecisionServiceFactory.Create();
            if (service == null)
            {
                _outcome = TestOutcome.Unavailable;
                return;
            }

            DecisionPlan plan = DecisionPlan.Of(DecisionQuestion.Choice(
                QuestionId,
                "Which color is the sky on a clear day?",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["blue"] = "The sky is blue.",
                    ["green"] = "The sky is green."
                }));
            using var cts = new CancellationTokenSource(Timeout);
            DecisionResult result = await service.EvaluateAsync("A clear day at noon.", plan, cts.Token).ConfigureAwait(false);
            timer.Stop();

            if (!result.TryGet(QuestionId, out DecisionAnswer answer))
            {
                _outcome = TestOutcome.NoAnswer;
                return;
            }
            _detail = (answer.Value ?? "?") + " · " + timer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
            _outcome = TestOutcome.Ok;
        }
        catch (Exception ex)
        {
            _detail = ex.Message;
            _outcome = TestOutcome.Failed;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}

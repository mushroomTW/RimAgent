"""RimAgent 運行紀錄統計：工具成功率、常見失敗原因、模型與版本比較。

用法：
    python run_log_stats.py [紀錄檔] [--since 2026-11-01] [--model 子字串] [--version 子字串] [--top 15]

紀錄檔預設為 %USERPROFILE%/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/RimAgent/RimAgent-runs.jsonl。
停止／取消造成的中斷不算工具失敗（舊紀錄沒有 Cancelled 欄位時，以例外名稱判斷）。
每筆紀錄的 TranscriptSteps 只保留最後 40 步，各工具明細只涵蓋保留下來的步驟；
新版紀錄的 ToolCalls/ToolFailures/ToolCancellations 才是整句的完整計數。
"""
import argparse
import collections
import json
import os
import re
import sys

DEFAULT_LOG = os.path.join(
    os.path.expanduser("~"), "AppData", "LocalLow", "Ludeon Studios",
    "RimWorld by Ludeon Studios", "RimAgent", "RimAgent-runs.jsonl")
CANCEL_PATTERN = re.compile(r"^(TaskCanceledException|OperationCanceledException)\b")


def is_cancelled(step):
    return step.get("Cancelled") or bool(CANCEL_PATTERN.match(step.get("Result") or ""))


def run_models(run):
    models = {m.get("Model") for m in run.get("ModelSteps", []) if m.get("Model")}
    return ", ".join(sorted(models)) or "(unknown)"


def normalize_error(text):
    return re.sub(r"\d+", "N", (text or "").splitlines()[0] if text else "")[:110]


def pct(part, whole):
    return f"{part / whole * 100:5.1f}%" if whole else "    -"


def load(path, args):
    runs = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            run = json.loads(line)
            if args.since and run.get("StartedUtc", "") < args.since:
                continue
            if args.model and args.model.lower() not in run_models(run).lower():
                continue
            if args.version and args.version not in (run.get("ModVersion") or ""):
                continue
            runs.append(run)
    return runs


def tool_steps(run):
    return [s for s in run.get("TranscriptSteps", []) if s.get("Tool")]


def run_counts(run):
    """整句的工具呼叫、失敗、取消數；舊紀錄沒有計數欄位時改由保留的步驟推算。"""
    if "ToolCalls" in run:
        return run["ToolCalls"], run["ToolFailures"], run["ToolCancellations"]
    steps = tool_steps(run)
    cancelled = sum(1 for s in steps if is_cancelled(s))
    failed = sum(1 for s in steps if s.get("Failed") and not is_cancelled(s))
    return len(steps), failed, cancelled


def print_group(title, runs, key):
    groups = collections.defaultdict(list)
    for run in runs:
        groups[key(run)].append(run)
    print(f"\n== {title}")
    print(f"{'':32}{'runs':>5}{'calls':>7}{'fail%':>8}{'finished':>10}{'avg in tok':>12}")
    for name, items in sorted(groups.items()):
        calls = fails = 0
        for run in items:
            c, f, _ = run_counts(run)
            calls += c
            fails += f
        finished = sum(1 for r in items if r.get("Outcome") in ("finished", "goal_reported_complete"))
        tokens = sum(sum(m.get("InputTokens") or 0 for m in r.get("ModelSteps", [])) for r in items) // len(items)
        print(f"{name[:31]:32}{len(items):5}{calls:7}{pct(fails, calls):>8}{pct(finished, len(items)):>10}{tokens:12}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("log", nargs="?", default=DEFAULT_LOG)
    parser.add_argument("--since", help="只算 StartedUtc 不早於此日期（ISO 格式）的紀錄")
    parser.add_argument("--model", help="只算用過此模型（子字串）的紀錄")
    parser.add_argument("--version", help="只算 ModVersion 含此字串的紀錄")
    parser.add_argument("--top", type=int, default=15, help="各表最多列幾行")
    args = parser.parse_args()

    if not os.path.exists(args.log):
        sys.exit(f"找不到紀錄檔：{args.log}")
    runs = load(args.log, args)
    if not runs:
        sys.exit("沒有符合條件的紀錄。")

    print(f"{len(runs)} runs, {runs[0]['StartedUtc'][:16]} ~ {runs[-1]['StartedUtc'][:16]}")
    outcomes = collections.Counter(r.get("Outcome", "") for r in runs)
    print("outcomes:", ", ".join(f"{k} {v}" for k, v in outcomes.most_common()))

    calls = fails = cancels = 0
    for run in runs:
        c, f, x = run_counts(run)
        calls, fails, cancels = calls + c, fails + f, cancels + x
    print(f"tool calls {calls}, failed {fails} ({pct(fails, calls).strip()}), cancelled {cancels}")

    total = collections.Counter()
    failed = collections.Counter()
    durations = collections.defaultdict(list)
    reasons = collections.defaultdict(collections.Counter)
    for run in runs:
        for step in tool_steps(run):
            if is_cancelled(step):
                continue
            name = step["Tool"]
            total[name] += 1
            if step.get("DurationMs"):
                durations[name].append(step["DurationMs"])
            if step.get("Failed"):
                failed[name] += 1
                reasons[name][normalize_error(step.get("Result"))] += 1

    print("\n== tools (kept steps only, cancellations excluded)")
    print(f"{'tool':28}{'calls':>6}{'fail':>6}{'fail%':>8}{'avg ms':>8}")
    for name, n in total.most_common(args.top):
        ms = durations[name]
        avg = f"{sum(ms) // len(ms):8}" if ms else "       -"
        print(f"{name:28}{n:6}{failed[name]:6}{pct(failed[name], n):>8}{avg}")

    print("\n== top failure reasons")
    for name, n in failed.most_common(args.top):
        print(f"{name} ({n}/{total[name]})")
        for reason, count in reasons[name].most_common(3):
            print(f"    {count:3}  {reason}")

    print_group("by model", runs, run_models)
    print_group("by mod version", runs, lambda r: r.get("ModVersion") or "(unknown)")


if __name__ == "__main__":
    main()

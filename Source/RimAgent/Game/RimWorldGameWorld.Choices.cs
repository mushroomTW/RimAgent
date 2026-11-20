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
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    // 抉擇對話框：DiaOption 的文字與觸發、對話框目前的節點與標題都是 protected。
    // 只讀取、觸發遊戲原生的選項；找不到成員時視為沒有可回應的抉擇，避免版本差異中斷 agent。
    #pragma warning disable S3011 // RimWorld 私有欄位與方法存取為 Mod 運作必需
    private static readonly FieldInfo DiaOptionTextField =
        typeof(DiaOption).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo DiaOptionActivateMethod =
        typeof(DiaOption).GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo NodeTreeCurNodeField =
        typeof(Dialog_NodeTree).GetField("curNode", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo NodeTreeTitleField =
        typeof(Dialog_NodeTree).GetField("title", BindingFlags.Instance | BindingFlags.NonPublic);

    #pragma warning restore S3011
    private const string LetterChoicePrefix = "letter-";

    private const string DialogChoicePrefix = "dialog-";

    private const int ChoiceTextLimit = 600;

    private const string ChoiceNotFound = "no pending choice with that id. Call list_pending_choices for the current ids.";

    public IReadOnlyList<PendingChoiceInfo> GetPendingChoices()
    {
        List<PendingChoiceInfo> result = [.. PendingUiChoices()];
        foreach (Dialog_NodeTree dialog in Find.WindowStack.Windows.OfType<Dialog_NodeTree>())
        {
            DiaNode node = CurrentNode(dialog);
            if (node == null) continue;
            result.Add(new PendingChoiceInfo
            {
                Id = DialogChoicePrefix + dialog.ID.ToString(CultureInfo.InvariantCulture),
                Title = NodeTreeTitleField?.GetValue(dialog) as string ?? string.Empty,
                Text = LimitChoiceText(node.text.Resolve()),
                Options = [.. ToChoiceOptions([.. node.options])]
            });
        }
        foreach (Dialog_MessageBox box in Find.WindowStack.Windows.OfType<Dialog_MessageBox>())
        {
            result.Add(new PendingChoiceInfo
            {
                Id = DialogChoicePrefix + box.ID.ToString(CultureInfo.InvariantCulture),
                Title = box.title ?? string.Empty,
                Text = LimitChoiceText(box.text.Resolve()),
                Options = [.. MessageBoxButtons(box).Select(b => new ChoiceOptionInfo { Number = b.Number, Text = b.Text })]
            });
        }
        int now = Find.TickManager.TicksGame;
        foreach (ChoiceLetter letter in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter>())
        {
            if (!IsDecisionLetter(letter)) continue;
            result.Add(new PendingChoiceInfo
            {
                Id = LetterChoicePrefix + letter.ID.ToString(CultureInfo.InvariantCulture),
                Title = letter.Label.Resolve(),
                Text = LimitChoiceText(letter.Text.Resolve()),
                ExpiresInTicks = letter.TimeoutActive ? letter.disappearAtTick - now : (int?)null,
                Options = [.. ToChoiceOptions([.. letter.Choices])]
            });
        }
        return result;
    }

    private static CommandResult AnswerDialogOrLetter(string id, int option)
    {
        if (TryParseChoiceId(id, DialogChoicePrefix, out int windowId))
        {
            Window window = Find.WindowStack.Windows.FirstOrDefault(w => w.ID == windowId);
            if (window is Dialog_MessageBox box) return PressMessageBoxButton(box, option);
            return window is Dialog_NodeTree dialog ? ActivateOption(dialog, option, closeOnFail: false) : CommandResult.Fail(ChoiceNotFound);
        }
        if (TryParseChoiceId(id, LetterChoicePrefix, out int letterId))
        {
            ChoiceLetter letter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter>()
                .FirstOrDefault(l => l.ID == letterId && IsDecisionLetter(l));
            if (letter == null) return CommandResult.Fail(ChoiceNotFound);
            // 照玩家的流程：打開信件，由遊戲組好對話框並設好每個選項的歸屬，再按選項。
            var before = new HashSet<Window>(Find.WindowStack.Windows);
            letter.OpenLetter();
            Dialog_NodeTree dialog = Find.WindowStack.Windows.OfType<Dialog_NodeTree>().LastOrDefault(w => !before.Contains(w));
            return dialog == null
                ? CommandResult.Fail("the letter did not open a choice dialog")
                : ActivateOption(dialog, option, closeOnFail: true);
        }
        return CommandResult.Fail(ChoiceNotFound);
    }

    /// <summary>
    /// 需要玩家做決定的信件。一般通知、死訊只有關閉／跳轉之類的選項；
    /// 任務邀請要到任務分頁接受，信件本身沒有接受選項。
    /// </summary>
    private static bool IsDecisionLetter(ChoiceLetter letter)
    {
        return !(letter is StandardLetter || letter is DeathLetter || letter is NewQuestLetter);
    }

    private static bool TryParseChoiceId(string id, string prefix, out int number)
    {
        number = 0;
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(id[prefix.Length..], CultureInfo.InvariantCulture, out number);
    }

    private static DiaNode CurrentNode(Dialog_NodeTree dialog)
    {
        return NodeTreeCurNodeField?.GetValue(dialog) as DiaNode;
    }

    private static string LimitChoiceText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= ChoiceTextLimit ? text : text[..ChoiceTextLimit] + "…";
    }

    // CA1859：參數用 List<T>、傳回 IEnumerable<T>，避免 IList<T> / IReadOnlyList<T>。
    private static List<ChoiceOptionInfo> ToChoiceOptions(List<DiaOption> options)
    {
        var result = new List<ChoiceOptionInfo>();
        for (int i = 0; i < options.Count; i++)
        {
            DiaOption option = options[i];
            // 資訊卡連結只開說明視窗，不是抉擇。
            if (option.hyperlink.def != null) continue;
            result.Add(new ChoiceOptionInfo
            {
                Number = i + 1,
                Text = OptionText(option),
                Disabled = option.disabled,
                DisabledReason = option.disabledReason ?? string.Empty
            });
        }
        return result;
    }

    private static string OptionText(DiaOption option)
    {
        return DiaOptionTextField?.GetValue(option) as string ?? "?";
    }

    /// <summary>確認框的按鈕：1＝A（通常是確定）、2＝B（通常是取消）、3＝C；沒有的按鈕不列。</summary>
    private static List<(int Number, string Text, Action Action, bool Close)> MessageBoxButtons(Dialog_MessageBox box)
    {
        var buttons = new List<(int, string, Action, bool)> { (1, box.buttonAText ?? "OK", box.buttonAAction, true) };
        if (box.buttonBText != null) buttons.Add((2, box.buttonBText, box.buttonBAction, true));
        if (!box.buttonCText.NullOrEmpty()) buttons.Add((3, box.buttonCText, box.buttonCAction, box.buttonCClose));
        return buttons;
    }

    /// <summary>照 Dialog_MessageBox 按鈕被點的順序：先跑按鈕動作，再關視窗。</summary>
    private static CommandResult PressMessageBoxButton(Dialog_MessageBox box, int option)
    {
        foreach ((int number, string text, Action action, bool close) in MessageBoxButtons(box))
        {
            if (number != option) continue;
            action?.Invoke();
            if (close) box.Close();
            return CommandResult.Ok(text);
        }
        return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"option {option} does not exist"));
    }

    private static CommandResult ActivateOption(Dialog_NodeTree dialog, int option, bool closeOnFail)
    {
        if (DiaOptionActivateMethod == null)
        {
            if (closeOnFail) dialog.Close(doCloseSound: false);
            return CommandResult.Fail("this game version does not expose dialog options");
        }
        List<DiaOption> options = CurrentNode(dialog)?.options;
        DiaOption chosen = options != null && option >= 1 && option <= options.Count ? options[option - 1] : null;
        string error = null;
        if (chosen == null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"option {option} does not exist");
        }
        else if (chosen.hyperlink.def != null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"option {option} only opens an info card");
        }
        else if (chosen.disabled)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"option {option} is disabled") + (string.IsNullOrEmpty(chosen.disabledReason) ? string.Empty : ": " + chosen.disabledReason);
        }
        if (error != null)
        {
            // 為了按選項才打開的信件對話框要關回去，不留一個擋住遊戲的視窗。
            if (closeOnFail) dialog.Close(doCloseSound: false);
            return CommandResult.Fail(error);
        }
        string label = OptionText(chosen);
        DiaOptionActivateMethod.Invoke(chosen, parameters: null);
        return CommandResult.Ok(label);
    }
}

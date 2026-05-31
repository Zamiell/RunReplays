using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays.Commands;

/// <summary>
/// Select a card from NCardRewardSelectionScreen, or sacrifice (Pael's Wing).
/// Recorded as: "TakeCard {index} # {cardTitle}"
///          or: "TakeCard sacrifice # {optionId}"
///
/// Follows a ClaimReward command that opened the card selection screen.
/// </summary>
public class TakeCardCommand : ReplayCommand
{
    private const string Prefix = "TakeCard ";
    private const string SacrificeKeyword = "sacrifice";
    private const string SkipKeyword = "skip";

    private static readonly FieldInfo? CardRowField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_cardRow", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? ExtraOptionsField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_extraOptions", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? OnAlternateRewardSelectedMethod =
        typeof(NCardRewardSelectionScreen).GetMethod(
            "OnAlternateRewardSelected",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public int CardIndex { get; }
    public bool IsSacrifice { get; }
    public bool IsSkip { get; }

    public TakeCardCommand(int cardIndex) : base("")
    {
        CardIndex = cardIndex;
    }

    private TakeCardCommand(bool sacrifice, bool skip) : base("")
    {
        CardIndex = -1;
        IsSacrifice = sacrifice;
        IsSkip = skip;
    }

    public static TakeCardCommand Sacrifice() => new(sacrifice: true, skip: false);
    public static TakeCardCommand Skip() => new(sacrifice: false, skip: true);

    public override string ToString()
        => IsSacrifice ? $"{Prefix}{SacrificeKeyword}"
         : IsSkip ? $"{Prefix}{SkipKeyword}"
         : $"{Prefix}{CardIndex}";

    public override string Describe()
        => IsSacrifice ? "sacrifice card reward"
         : IsSkip ? "skip card reward"
         : $"take card [{CardIndex}]" + (Comment != null ? $" ({Comment})" : "");

    public override ExecuteResult Execute()
    {
        var screen = ReplayState.CardRewardSelectionScreen;
        if (screen == null)
            return ExecuteResult.Retry(200);

        if (IsSacrifice)
            return ExecuteSacrifice(screen);

        if (IsSkip)
            return ExecuteSkip(screen);

        return ExecuteSelectCard(screen);
    }

    private ExecuteResult ExecuteSelectCard(NCardRewardSelectionScreen screen)
    {
        var cardRow = CardRowField?.GetValue(screen) as Godot.Node;
        if (cardRow == null)
            return ExecuteResult.Retry(200);

        // Collect card holders sorted by X position for correct visual order.
        var holders = new List<(Godot.Control Holder, CardModel Card)>();
        foreach (Godot.Node child in cardRow.GetChildren())
        {
            if (child is not Godot.Control ctrl) continue;
            var prop = child.GetType().GetProperty(
                "CardModel", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(child) is CardModel card)
                holders.Add((ctrl, card));
        }
        holders.Sort((a, b) => a.Holder.Position.X.CompareTo(b.Holder.Position.X));

        int selectedIndex = ResolveRecordedCardIndex(holders) ?? CardIndex;
        if (selectedIndex < 0 || selectedIndex >= holders.Count)
        {
            PlayerActionBuffer.LogMigrationWarning(
                $"[TakeCard] Index {selectedIndex} out of range (count={holders.Count}) — retrying.");
            return ExecuteResult.Retry(200);
        }

        var holder = holders[selectedIndex].Holder;
        holder.EmitSignal("Pressed", holder);
        PlayerActionBuffer.LogDispatcher(
            $"[TakeCard] Selected card [{selectedIndex}] ({holders[selectedIndex].Card.Title}).");
        ReplayState.CardRewardSelectionScreen = null;
        ReplayDispatcher.DispatchNow();
        return ExecuteResult.Ok();
    }

    private int? ResolveRecordedCardIndex(IReadOnlyList<(Godot.Control Holder, CardModel Card)> holders)
    {
        if (string.IsNullOrWhiteSpace(Comment)
            || Comment.Equals(SkipKeyword, System.StringComparison.OrdinalIgnoreCase)
            || Comment.Equals(SacrificeKeyword, System.StringComparison.OrdinalIgnoreCase))
            return null;

        string recorded = NormalizeCardTitle(Comment);
        if (recorded.Length == 0)
            return null;

        for (int i = 0; i < holders.Count; i++)
        {
            if (NormalizeCardTitle(holders[i].Card.Title) == recorded)
            {
                if (i != CardIndex)
                {
                    PlayerActionBuffer.LogDispatcher(
                        $"[TakeCard] Resolved recorded card '{Comment}' at index {i} instead of logged index {CardIndex}.");
                }
                return i;
            }
        }

        return null;
    }

    private static string NormalizeCardTitle(string value)
        => new(value
            .Where(ch => char.IsLetterOrDigit(ch))
            .Select(char.ToUpperInvariant)
            .ToArray());

    private ExecuteResult ExecuteSkip(NCardRewardSelectionScreen screen)
    {
        var extras = ExtraOptionsField?.GetValue(screen)
            as IReadOnlyList<CardRewardAlternative>;

        // Find the skip option (AfterSelected == EndSelectionAndDoNotCompleteReward).
        CardRewardAlternative? skipAlt = null;
        int skipIndex = -1;
        if (extras != null)
        {
            for (int i = 0; i < extras.Count; i++)
            {
                if (extras[i].AfterSelected == MegaCrit.Sts2.Core.Entities.Rewards.PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward)
                {
                    skipAlt = extras[i];
                    skipIndex = i;
                    break;
                }
            }
        }

        if (skipAlt != null)
        {
            TaskHelper.RunSafely(skipAlt.OnSelect());
            OnAlternateRewardSelectedMethod?.Invoke(screen, new object[] { skipIndex });
        }
        else
        {
            // Fallback: use index 0.
            OnAlternateRewardSelectedMethod?.Invoke(screen, new object[] { 0 });
        }

        ReplayState.CardRewardSelectionScreen = null;
        ReplayDispatcher.DispatchNow();
        return ExecuteResult.Ok();
    }

    private ExecuteResult ExecuteSacrifice(NCardRewardSelectionScreen screen)
    {
        var extras = ExtraOptionsField?.GetValue(screen)
            as IReadOnlyList<CardRewardAlternative>;

        if (extras == null || extras.Count == 0)
        {
            PlayerActionBuffer.LogMigrationWarning(
                "[TakeCard] No extra options on selection screen — retrying.");
            return ExecuteResult.Retry(200);
        }

        CardRewardAlternative? sacrifice = null;
        int sacrificeIndex = -1;
        for (int i = 0; i < extras.Count; i++)
        {
            if (extras[i].OptionId.Contains("sacrifice", System.StringComparison.OrdinalIgnoreCase)
                || extras[i].OptionId.Contains("pael", System.StringComparison.OrdinalIgnoreCase))
            {
                sacrifice = extras[i];
                sacrificeIndex = i;
                break;
            }
        }
        if (sacrifice == null)
        {
            sacrifice = extras[0];
            sacrificeIndex = 0;
        }

        TaskHelper.RunSafely(sacrifice.OnSelect());
        OnAlternateRewardSelectedMethod?.Invoke(screen, new object[] { sacrificeIndex });

        ReplayState.CardRewardSelectionScreen = null;
        ReplayDispatcher.DispatchNow();
        return ExecuteResult.Ok();
    }

    public static TakeCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string rest = raw.Substring(Prefix.Length).Trim();

        if (rest.Equals(SacrificeKeyword, System.StringComparison.OrdinalIgnoreCase))
            return TakeCardCommand.Sacrifice();

        if (rest.Equals(SkipKeyword, System.StringComparison.OrdinalIgnoreCase))
            return TakeCardCommand.Skip();

        if (int.TryParse(rest, out int index))
            return new TakeCardCommand(index);

        return null;
    }
}

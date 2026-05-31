using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace RunReplays.Patches.Record;
using RunReplays;
using RunReplays.Commands;

/// <summary>
/// Shared state for shop purchase recording.
///
/// IsPurchasing: suppresses duplicate SyncLocalObtained* recordings in
///   BattleRewardPatch while a merchant purchase is in progress.
///
/// PendingCommand: the Buy* command captured in the OnTryPurchaseWrapper prefix,
///   before ClearAfterPurchase() nulls out the entry's item reference.
///   Written by InvokePurchaseCompleted (on success) or cleared by
///   InvokePurchaseFailed (on failure).
/// </summary>
internal static class ShopPurchaseState
{
    internal static bool IsPurchasing;
    internal static ReplayCommand? PendingCommand;
}

/// <summary>
/// Recording patches for merchant shop interactions.
///
/// Item details (card title etc.) are read in the OnTryPurchaseWrapper PREFIX
/// because ClearAfterPurchase() nulls the entry's item reference before
/// InvokePurchaseCompleted fires.
///
/// InvokePurchaseCompleted prefix writes the captured label and clears state.
/// InvokePurchaseFailed prefix clears state so the flag never stays stuck.
///
/// MerchantCardRemovalEntry overrides OnTryPurchaseWrapper with an extra
/// parameter and is therefore given its own prefix patch.
/// </summary>

// ── Open shop ─────────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom.OpenInventory))]
public static class ShopOpenRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix(NMerchantRoom __instance)
    {
        // Only record when the inventory is actually about to open.
        // OpenInventory() is guarded by `if (!Inventory.IsOpen)` in the game,
        // so any call while already open is a no-op.  Without this check a
        // second call (e.g. from a UI refresh) would record a duplicate OpenShop
        // that later stalls ProcessNextPurchase during replay.
        if (__instance.Inventory?.IsOpen == false)
            PlayerActionBuffer.Record(new OpenShopCommand());
    }
}

// ── Capture item label and set flag on purchase start ─────────────────────────

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
public static class ShopPurchaseStartPatch
{
    [HarmonyPrefix]
    public static void Prefix(MerchantEntry __instance)
    {
        // Record immediately so the command appears in the log as soon as
        // the player clicks.  If the purchase fails, UndoLast removes it.
        ReplayCommand? command = __instance switch
        {
            MerchantCardEntry card     => card.CreationResult?.Card?.Title is string t
                                             ? new BuyCardCommand(t) : null,
            MerchantRelicEntry relic   => relic.Model  != null
                                             ? new BuyRelicCommand(relic.Model.Title.GetFormattedText())  : null,
            MerchantPotionEntry potion => potion.Model != null
                                             ? new BuyPotionCommand(potion.Model.Title.GetFormattedText()) : null,
            _ => null
        };
        if (command != null)
            PlayerActionBuffer.Record(command);
        ShopPurchaseState.PendingCommand = command;
        ShopPurchaseState.IsPurchasing = true;
    }
}

[HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper))]
public static class ShopCardRemovalPurchaseStartPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        // Record immediately so BuyCardRemoval precedes RemoveCardFromDeck in the
        // log — the card-removal UI fires CardPileCmd.RemoveFromDeck before
        // InvokePurchaseCompleted, so recording at completion would invert the order.
        // PendingCommand is left null so ShopPurchaseCompletedPatch skips re-recording.
        PlayerActionBuffer.Record(new BuyCardRemovalCommand());
        ShopPurchaseState.PendingCommand = null;
        ShopPurchaseState.IsPurchasing = true;
    }
}

// ── Record on success and clear state ────────────────────────────────────────

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted))]
public static class ShopPurchaseCompletedPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        // Already recorded at purchase start — just clear state.
        ShopPurchaseState.IsPurchasing = false;
        ShopPurchaseState.PendingCommand = null;
    }
}

// ── Clear state on failure ────────────────────────────────────────────────────

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseFailed))]
public static class ShopPurchaseFailedPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        // Purchase failed — undo the speculatively recorded entry.
        if (ShopPurchaseState.PendingCommand != null)
            PlayerActionBuffer.UndoLast();
        ShopPurchaseState.IsPurchasing = false;
        ShopPurchaseState.PendingCommand = null;
    }
}

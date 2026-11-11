using System;
using System.Collections.Generic;
using HarmonyLib;

namespace TheFlea
{
    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SellItem))]
    internal static class SellItemPatch
    {
        private static bool Prefix(PlayerStore __instance, GameItem item)
        {
            if (!TraderVisit.IsCurrentClient(__instance))
            {
                return true;
            }
            try
            {
                TraderVisit.Consign(item);
            }
            catch (Exception e)
            {
                ModEntry.Log.Error($"Could not consign {item?.identifier}: {e}");
            }
            return false;
        }
    }

    // Exposes what the trader catches first, so the game's own customer reaction finds nothing left to do.
    [HarmonyPatch(typeof(StoreClient), nameof(StoreClient.OnItemPlaced))]
    internal static class ItemPlacedPatch
    {
        private static void Prefix(StoreClient __instance, GameItem gameItem)
        {
            try
            {
                if (__instance.identifier == TraderVisit.ClientId)
                {
                    FakeCheck.Inspect(__instance, gameItem, speak: true);
                }
            }
            catch (Exception e)
            {
                PatchErrors.LogOnce(nameof(ItemPlacedPatch), e);
            }
        }
    }

    // Moving an item around the counter strips event lines and re-adds only the real events.
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.AddFeatureRelatedToEvent))]
    internal static class EventFeaturePatch
    {
        private static void Postfix(GameItem __instance)
        {
            try
            {
                TraderVisit.ReplaceEventPrices(__instance);
            }
            catch (Exception e)
            {
                PatchErrors.LogOnce(nameof(EventFeaturePatch), e);
            }
        }
    }

    // Items inside a container don't have event lines: their value reads active events directly.
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.GetItemMiscValue))]
    internal static class ContainedEventPricePatch
    {
        private static void Prefix(ref bool includeEvents)
        {
            includeEvents = includeEvents && !TraderVisit.IsTraderPresent();
        }
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.GetCurrentValue))]
    internal static class CurrentEventPricePatch
    {
        private static void Prefix(ref bool includeEvents)
        {
            includeEvents = includeEvents && !TraderVisit.IsTraderPresent();
        }
    }

    // Lets items inside an offered container follow the market price, the way perks already reach them.
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.GetInheritableClientMarketMod))]
    internal static class ContainedMarketPricePatch
    {
        private static void Postfix(GameItem __instance, ref long __result)
        {
            __result += TraderVisit.MarketPercent(__instance);
        }
    }

    internal static class PatchErrors
    {
        private static readonly HashSet<string> logged = new HashSet<string>();

        public static void LogOnce(string patch, Exception e)
        {
            if (logged.Add(patch))
            {
                ModEntry.Log.Error($"{patch} failed and will keep being skipped: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(StorePhoneClient), nameof(StorePhoneClient.GetLocalizedDisplayName))]
    internal static class PhoneNamePatch
    {
        private static bool Prefix(StorePhoneClient __instance, ref string __result)
        {
            if (__instance.dialogFuncId != PhoneContact.DialogueId)
            {
                return true;
            }
            __result = ModEntry.L("trader_name");
            return false;
        }
    }
}

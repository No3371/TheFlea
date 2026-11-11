using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace TheFlea
{
    // The batch lives in the save, so a rollback restores sold items and their unpaid batch together.
    internal static class BatchStore
    {
        private const string Key = "pending_batch";
        private const string CallKey = "pending_call";
        private const string PurchasesKey = "unreported_purchases";
        private const string FailedDaysKey = "failed_days";
        private const string ClientIdFile = "client_id.txt";
        private const string RatesFile = "market_rates.json";

        public static PendingBatch Load()
        {
            string json = ModHelper.GetModData(ModEntry.ModId, Key);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            PendingBatch batch = JsonConvert.DeserializeObject<PendingBatch>(json);
            return batch.items.Count > 0 ? batch : null;
        }

        public static void Add(PendingItem item)
        {
            PendingBatch batch = Load() ?? new PendingBatch { batch_id = Guid.NewGuid().ToString("N"), market = ModEntry.MarketCode() };
            batch.items.Add(item);
            ModHelper.SetModData(ModEntry.ModId, Key, JsonConvert.SerializeObject(batch));
        }

        public static void RemoveItem(PendingItem item)
        {
            PendingBatch batch = Load();
            int index = batch?.items.FindLastIndex(saved => saved.payload == item.payload && saved.item_id == item.item_id) ?? -1;
            if (index >= 0)
            {
                batch.items.RemoveAt(index);
                ModHelper.SetModData(ModEntry.ModId, Key, JsonConvert.SerializeObject(batch));
            }
        }

        public static bool IsFull()
        {
            PendingBatch batch = Load();
            return batch != null && batch.items.Count >= TraderLevel.Items();
        }

        public static void Clear()
        {
            ModHelper.RemoveModData(ModEntry.ModId, Key);
        }

        // Real listings bought and not yet reported. Kept in the save, so a reload undoes a purchase and its record together.
        public static List<string> UnreportedPurchases()
        {
            try
            {
                return Purchases.Parse(ModHelper.GetModData(ModEntry.ModId, PurchasesKey));
            }
            catch (Exception e)
            {
                ModEntry.Log.Warning("Could not read unreported purchases: " + e.Message);
                return new List<string>();
            }
        }

        public static void AddPurchase(string listingId)
        {
            List<string> ids = UnreportedPurchases();
            if (!ids.Contains(listingId))
            {
                ids.Add(listingId);
                ModHelper.SetModData(ModEntry.ModId, PurchasesKey, JsonConvert.SerializeObject(ids));
            }
        }

        public static void RemovePurchases(List<string> reported)
        {
            if (reported.Count == 0)
            {
                return;
            }
            List<string> ids = UnreportedPurchases().Except(reported).ToList();
            if (ids.Count > 0)
            {
                ModHelper.SetModData(ModEntry.ModId, PurchasesKey, JsonConvert.SerializeObject(ids));
            }
            else
            {
                ModHelper.RemoveModData(ModEntry.ModId, PurchasesKey);
            }
        }

        // Counts today as a failed day, once per game day, and returns how many failed days have run in a row.
        public static int CountFailedDay()
        {
            int count = FailedDays.Count(ModHelper.GetModData(ModEntry.ModId, FailedDaysKey), StoreStation.Instance.dayCounter, out string store);
            if (store != null)
            {
                ModHelper.SetModData(ModEntry.ModId, FailedDaysKey, store);
            }
            return count;
        }

        public static void ClearFailedDays()
        {
            ModHelper.RemoveModData(ModEntry.ModId, FailedDaysKey);
        }

        private static MarketRates rates;
        private static bool ratesRead;

        // Kept outside the save, like the client ID: the rates describe the market, not one run.
        public static MarketRates SaveRates(string market, Dictionary<string, float> categories, float fee)
        {
            rates = new MarketRates
            {
                market = market,
                categories = categories ?? new Dictionary<string, float>(),
                fee = fee,
                saved_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            ratesRead = true;
            try
            {
                ModHelper.WriteModDataFileText(ModEntry.ModId, RatesFile, JsonConvert.SerializeObject(rates));
            }
            catch (Exception e)
            {
                ModEntry.Log.Warning("Could not save market rates: " + e.Message);
            }
            return rates;
        }

        // The cached rates of this market, or null. A missing or damaged file means no cache.
        public static MarketRates LoadRates(string market)
        {
            if (!ratesRead)
            {
                ratesRead = true;
                try
                {
                    if (ModHelper.ModDataFileExists(ModEntry.ModId, RatesFile))
                    {
                        rates = RatesCache.Parse(ModHelper.ReadModDataFileText(ModEntry.ModId, RatesFile));
                        if (rates == null)
                        {
                            ModEntry.Log.Warning("Cached market rates are damaged and are ignored.");
                        }
                    }
                }
                catch (Exception e)
                {
                    ModEntry.Log.Warning("Could not read cached market rates: " + e.Message);
                    rates = null;
                }
            }
            return RatesCache.ForMarket(rates, market);
        }

        // A call stays pending until the trader actually arrives, so a shop closed early only delays the visit.
        // Calling again changes the order but keeps the day, so it can't hurry or put off a trader who's coming.
        public static void SaveCall(string category, int day)
        {
            if (TryReadCall(out int savedDay, out _))
            {
                day = savedDay;
            }
            ModHelper.SetModData(ModEntry.ModId, CallKey, Calls.Format(day, category));
        }

        // The day the trader will come if called now. The roll depends only on this install and the day,
        // so reloading and calling again gets the same answer.
        public static int ArrivalDay()
        {
            int today = StoreStation.Instance.dayCounter;
            if (TryReadCall(out int savedDay, out _))
            {
                return savedDay;
            }
            return today + Calls.Delay(Roll(today, Calls.RollRange));
        }

        public static bool TryGetDueCall(out string category)
        {
            return TryReadCall(out int day, out category) && StoreStation.Instance.dayCounter >= day;
        }

        private static bool TryReadCall(out int day, out string category)
        {
            return Calls.TryParse(ModHelper.GetModData(ModEntry.ModId, CallKey), out day, out category);
        }

        public static void ClearCall()
        {
            ModHelper.RemoveModData(ModEntry.ModId, CallKey);
        }

        // A number in [0, range) fixed for this install and key, so reloading can't re-roll it.
        public static int Roll(int key, int range) => Rolls.Roll(ClientId(), key, range);

        private static string clientId;

        // Kept outside the save: it identifies this install, not one run.
        // The server refuses any other form, so a damaged or edited file is replaced.
        public static string ClientId()
        {
            if (clientId != null)
            {
                return clientId;
            }
            if (ModHelper.ModDataFileExists(ModEntry.ModId, ClientIdFile))
            {
                clientId = ModHelper.ReadModDataFileText(ModEntry.ModId, ClientIdFile)?.Trim();
            }
            if (!Identity.IsValidClientId(clientId))
            {
                clientId = Guid.NewGuid().ToString("N");
                ModHelper.WriteModDataFileText(ModEntry.ModId, ClientIdFile, clientId);
            }
            return clientId;
        }
    }
}

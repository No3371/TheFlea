using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

// Rules that don't touch the game or Unity. The test project compiles this folder and Protocol.cs on their own.
namespace TheFlea
{
    internal static class Pricing
    {
        public const float UnknownPriceMultiplier = 0.5f;
        // The server keeps prices between half and one and a half times the value. These bounds are looser,
        // only so a broken or hostile server can't hand out fortunes or take the player's money.
        public const float MinMultiplier = 0.25f;
        public const float MaxMultiplier = 2f;
        public const float MaxFee = 0.5f;
        public const double RateHalfLifeHours = 2;

        public static float SafeMultiplier(float multiplier, float fallback)
        {
            return float.IsNaN(multiplier) || float.IsInfinity(multiplier) ? fallback : Math.Clamp(multiplier, MinMultiplier, MaxMultiplier);
        }

        public static float SafeFee(float fee)
        {
            return float.IsNaN(fee) ? 0f : Math.Clamp(fee, 0f, MaxFee);
        }

        // The average rate of the item's categories that have one, or null when none do.
        public static float? CategoryRate(IEnumerable<string> types, IDictionary<string, float> rates)
        {
            if (rates == null)
            {
                return null;
            }
            List<float> known = (types ?? Enumerable.Empty<string>())
                .Where(type => type != null && rates.ContainsKey(type))
                .Select(type => rates[type])
                .ToList();
            return known.Count > 0 ? known.Average() : (float?)null;
        }

        // The server lets pressure fade by half every 2 real hours, so a cached rate fades toward 1x the same way.
        public static Dictionary<string, float> FadedRates(MarketRates cached, long nowSeconds)
        {
            double hours = Math.Max(0, nowSeconds - cached.saved_at) / 3600.0;
            double keep = Math.Pow(0.5, hours / RateHalfLifeHours);
            Dictionary<string, float> faded = new Dictionary<string, float>();
            foreach (var rate in cached.categories ?? new Dictionary<string, float>())
            {
                if (rate.Key != null && rate.Value > 0f && !float.IsInfinity(rate.Value))
                {
                    faded[rate.Key] = (float)Math.Exp(Math.Log(rate.Value) * keep);
                }
            }
            return faded;
        }

        // Each item follows the rates of its own categories, less the trader's cut.
        public static long LocalSale(PendingBatch batch, IDictionary<string, float> rates, float fee)
        {
            float cut = 1f - SafeFee(fee);
            long total = 0;
            foreach (PendingItem item in batch.items)
            {
                float rate = CategoryRate(item.types, rates) ?? 1f;
                total += (long)(item.value * SafeMultiplier(rate * cut, UnknownPriceMultiplier));
            }
            return Math.Min(total, int.MaxValue);
        }

        public static long GrossSale(PendingBatch batch, SalesResponse response)
        {
            Dictionary<string, float> prices = new Dictionary<string, float>();
            foreach (var price in response?.prices ?? Enumerable.Empty<PriceEntry>())
            {
                if (price?.item_id != null)
                {
                    prices[price.item_id] = SafeMultiplier(price.sell_multiplier, UnknownPriceMultiplier);
                }
            }
            long total = 0;
            foreach (PendingItem item in batch.items)
            {
                float multiplier = item.item_id != null && prices.TryGetValue(item.item_id, out float known) ? known : UnknownPriceMultiplier;
                total += (long)(item.value * multiplier);
            }
            return Math.Min(total, int.MaxValue);
        }

        // What the batch was worth when handed over, and how far the payout is above it.
        public static (long Worth, long Difference) Compare(PendingBatch batch, long payout)
        {
            long worth = batch.items.Sum(item => item.value);
            return (worth, payout - worth);
        }

        public static int Percent(float multiplier)
        {
            return (int)Math.Round((multiplier - 1f) * 100f, MidpointRounding.ToEven);
        }
    }

    internal static class Levels
    {
        // He takes as many items as he brings offers.
        private static readonly (long Credits, int Offers)[] Table =
        {
            (0, 4),
            (2500, 6),
            (7500, 8),
            (20000, 10),
            (50000, 12)
        };

        public static (long Credits, int Offers) At(long credits)
        {
            return Table.Last(level => credits >= level.Credits);
        }

        // The new level's offers if this amount takes the player to a new level, otherwise null.
        public static int? UpAfter(long credits, long amount)
        {
            var level = At(credits + Math.Max(amount, 0));
            return level.Credits > At(credits).Credits ? level.Offers : (int?)null;
        }
    }

    internal static class FailedDays
    {
        // Counts today as a failed day, once per game day. Returns the run of failed days and what to store,
        // or null to store when today was already counted.
        public static int Count(string stored, int today, out string store)
        {
            string[] parts = (stored ?? "").Split('|');
            int count = 0;
            if (parts.Length == 2 && int.TryParse(parts[0], out int saved) && int.TryParse(parts[1], out int day) && saved > 0)
            {
                if (day == today)
                {
                    store = null;
                    return saved;
                }
                count = saved;
            }
            count++;
            store = $"{count}|{today}";
            return count;
        }
    }

    internal static class Calls
    {
        // Days the trader takes to come, with their weights out of 10.
        private static readonly (int Days, int Weight)[] Delays = { (1, 6), (2, 3), (3, 1) };
        public const int RollRange = 10;

        public static int Delay(int roll)
        {
            foreach (var delay in Delays)
            {
                if (roll < delay.Weight)
                {
                    return delay.Days;
                }
                roll -= delay.Weight;
            }
            return 1;
        }

        public static string Format(int day, string category) => $"{day}|{category}";

        public static bool TryParse(string call, out int day, out string category)
        {
            day = 0;
            category = null;
            if (string.IsNullOrEmpty(call))
            {
                return false;
            }
            string[] parts = call.Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[0], out day))
            {
                return false;
            }
            category = parts[1];
            return true;
        }
    }

    internal static class Rolls
    {
        // A number in [0, range) fixed for this install and key, so reloading can't re-roll it.
        // FNV-1a over the install ID and the key's bytes; string.GetHashCode isn't stable across runs.
        public static int Roll(string clientId, int key, int range)
        {
            uint hash = 2166136261;
            foreach (char c in clientId ?? "")
            {
                hash = (hash ^ c) * 16777619;
            }
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash = (hash ^ (byte)(key >> shift)) * 16777619;
            }
            return (int)(hash % (uint)range);
        }
    }

    internal static class Identity
    {
        public const string DefaultMarket = "0";
        private const int MaxMarketCodeLength = 32;
        private const int MaxSellerLength = 24;
        private const int MaxExtraNameLength = 32;

        // Exactly 32 lowercase hex characters, the form Guid.ToString("N") writes. The server refuses any other form.
        public static bool IsValidClientId(string id)
        {
            return id != null && id.Length == 32 && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        // Characters the server would reject are dropped, so a typo in the settings can't stop the trader from coming.
        public static string MarketCode(string setting)
        {
            string code = new string((setting ?? "").ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')
                .Take(MaxMarketCodeLength)
                .ToArray());
            return code.Length > 0 ? code : DefaultMarket;
        }

        // Names come from other players, so markup that could restyle the tooltip is removed.
        public static string CleanSellerName(string name)
        {
            string clean = new string((name ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray()).Trim();
            return clean.Length > MaxSellerLength ? clean.Substring(0, MaxSellerLength).Trim() : clean;
        }

        // Keeps names the game builds around the item's own, like "Large X" or "X (Counterfeit)".
        public static bool IsPlausibleName(string name, string freshName)
        {
            return !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(freshName)
                && name.Contains(freshName)
                && name.Length <= freshName.Length + MaxExtraNameLength
                && !name.Any(c => char.IsControl(c) || c == '<' || c == '>');
        }
    }

    internal static class RatesCache
    {
        // A missing or damaged file means no cache.
        public static MarketRates Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            try
            {
                return JsonConvert.DeserializeObject<MarketRates>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static MarketRates ForMarket(MarketRates rates, string market)
        {
            return rates != null && rates.market == market && rates.categories != null ? rates : null;
        }
    }

    // Unity's JsonUtility silently drops lists of this mod's own classes, which lost every fake mark,
    // so the envelope goes through Newtonsoft. The field names, and so the payload, are unchanged.
    internal static class Envelopes
    {
        // Text that isn't JSON throws, as before, and the caller skips that offer.
        public static ItemEnvelope Read(string payload) => JsonConvert.DeserializeObject<ItemEnvelope>(payload);

        public static string Write(ItemEnvelope envelope) => JsonConvert.SerializeObject(envelope);
    }

    internal static class Purchases
    {
        public static List<string> Parse(string json)
        {
            List<string> ids = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<List<string>>(json);
            return ids?.Where(id => !string.IsNullOrEmpty(id)).ToList() ?? new List<string>();
        }
    }

    internal static class Assortment
    {
        public const double ClosePriceRange = 0.25;
        public const double FarPriceRatio = 2;

        // Picks an item close in price, or the nearest within a factor of two, or nothing.
        // random(n) returns a number in [0, n).
        public static T Pick<T>(IEnumerable<T> matching, Func<T, long> value, double target, Func<int, int> random) where T : class
        {
            if (target <= 0)
            {
                return null;
            }
            List<T> all = matching.ToList();
            List<T> close = all.Where(entry => Math.Abs(value(entry) - target) <= target * ClosePriceRange).ToList();
            if (close.Count > 0)
            {
                return close[random(close.Count)];
            }
            T nearest = all.OrderBy(entry => Math.Abs(value(entry) - target)).FirstOrDefault();
            return nearest != null && value(nearest) <= target * FarPriceRatio && value(nearest) >= target / FarPriceRatio ? nearest : null;
        }

        // Items sharing a wanted type. Some items, like backpacks and shop signs, have no type at all,
        // so a sale of one is matched on price alone rather than never minted.
        public static IEnumerable<T> Matching<T>(IEnumerable<T> entries, Func<T, IEnumerable<string>> typesOf, List<string> wanted)
        {
            if (wanted == null || wanted.Count == 0)
            {
                return entries;
            }
            return entries.Where(entry => typesOf(entry).Intersect(wanted).Any());
        }

        // A sold item can carry minor types too, and those shouldn't decide what comes back.
        public static List<string> WantedTypes(List<string> offerTypes, string category, ICollection<string> majorTypes)
        {
            if (!string.IsNullOrEmpty(category))
            {
                return new List<string> { category };
            }
            List<string> types = offerTypes ?? new List<string>();
            List<string> major = types.Intersect(majorTypes).ToList();
            return major.Count > 0 ? major : types;
        }

        // The server keeps five categories, so the ones the trader can be asked for go first.
        public static List<string> SaleTypes(IEnumerable<string> itemTypes, ICollection<string> majorTypes, int max)
        {
            List<string> types = itemTypes.Distinct().ToList();
            return types.Where(majorTypes.Contains)
                .Concat(types.Where(type => !majorTypes.Contains(type)))
                .Take(max)
                .ToList();
        }
    }
}

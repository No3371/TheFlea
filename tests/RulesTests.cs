using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Xunit;

namespace TheFlea.Tests
{
    // Each class names the TESTING.md checks it stands in for.
    public class FallbackPricingTests
    {
        private static PendingBatch Batch(params (string Id, long Value, string[] Types)[] items)
        {
            return new PendingBatch
            {
                batch_id = "b",
                market = "0",
                items = items.Select(i => new PendingItem { item_id = i.Id, value = i.Value, types = i.Types.ToList() }).ToList()
            };
        }

        [Fact]
        public void Without_cached_rates_the_batch_is_paid_at_half()
        {
            PendingBatch batch = Batch(("a", 100, new[] { "FOOD" }), ("b", 51, new string[0]));
            Assert.Equal(50 + 25, Pricing.GrossSale(batch, new SalesResponse()));
        }

        [Fact]
        public void Server_prices_are_used_per_item_and_unknown_items_get_half()
        {
            PendingBatch batch = Batch(("a", 100, new string[0]), ("b", 100, new string[0]));
            SalesResponse response = new SalesResponse { prices = { new PriceEntry { item_id = "a", sell_multiplier = 1.2f } } };
            Assert.Equal(120 + 50, Pricing.GrossSale(batch, response));
        }

        [Theory]
        [InlineData(100f, 2f)]
        [InlineData(0f, 0.25f)]
        [InlineData(-3f, 0.25f)]
        public void Server_prices_are_clamped(float sent, float used)
        {
            PendingBatch batch = Batch(("a", 1000, new string[0]));
            SalesResponse response = new SalesResponse { prices = { new PriceEntry { item_id = "a", sell_multiplier = sent } } };
            Assert.Equal((long)(1000 * used), Pricing.GrossSale(batch, response));
        }

        [Fact]
        public void Broken_server_prices_fall_back_to_half()
        {
            PendingBatch batch = Batch(("a", 1000, new string[0]), ("b", 1000, new string[0]));
            SalesResponse response = new SalesResponse
            {
                prices = { new PriceEntry { item_id = "a", sell_multiplier = float.NaN }, new PriceEntry { item_id = "b", sell_multiplier = float.PositiveInfinity }, null }
            };
            Assert.Equal(1000, Pricing.GrossSale(batch, response));
        }

        [Fact]
        public void Gross_sale_never_exceeds_int_max()
        {
            PendingBatch batch = Batch(("a", long.MaxValue / 4, new string[0]));
            SalesResponse response = new SalesResponse { prices = { new PriceEntry { item_id = "a", sell_multiplier = 2f } } };
            Assert.Equal(int.MaxValue, Pricing.GrossSale(batch, response));
        }

        [Fact]
        public void Local_sale_uses_the_category_rates_less_the_cut()
        {
            PendingBatch batch = Batch(("a", 1000, new[] { "FOOD", "DRUG" }), ("b", 1000, new[] { "MISC" }));
            var rates = new Dictionary<string, float> { ["FOOD"] = 1.2f, ["DRUG"] = 0.8f };
            // a: average 1.0, b: no known category so 1.0. Both less a 10% cut.
            Assert.Equal(900 + 900, Pricing.LocalSale(batch, rates, 0.1f));
        }

        [Fact]
        public void Local_sale_without_rates_pays_plain_value_less_cut()
        {
            PendingBatch batch = Batch(("a", 1000, new[] { "FOOD" }));
            Assert.Equal(800, Pricing.LocalSale(batch, null, 0.2f));
        }

        [Theory]
        [InlineData(float.NaN, 0f)]
        [InlineData(-1f, 0f)]
        [InlineData(0.9f, 0.5f)]
        [InlineData(0.15f, 0.15f)]
        public void Fee_is_kept_sane(float sent, float used)
        {
            Assert.Equal(used, Pricing.SafeFee(sent));
        }

        [Fact]
        public void A_huge_fee_cant_take_more_than_the_floor()
        {
            PendingBatch batch = Batch(("a", 1000, new[] { "FOOD" }));
            var rates = new Dictionary<string, float> { ["FOOD"] = 0.3f };
            // 0.3 * (1 - 0.5) = 0.15, raised to the 0.25 floor.
            Assert.Equal(250, Pricing.LocalSale(batch, rates, 5f));
        }

        [Fact]
        public void Cached_rates_fade_by_half_every_two_hours()
        {
            var cached = new MarketRates { market = "0", categories = new Dictionary<string, float> { ["FOOD"] = 4f, ["DRUG"] = 0.25f }, saved_at = 1000 };
            var now = Pricing.FadedRates(cached, 1000);
            var later = Pricing.FadedRates(cached, 1000 + 2 * 3600);
            var much_later = Pricing.FadedRates(cached, 1000 + 200 * 3600);
            Assert.Equal(4f, now["FOOD"], 3);
            Assert.Equal(2f, later["FOOD"], 3);
            Assert.Equal(0.5f, later["DRUG"], 3);
            Assert.Equal(1f, much_later["FOOD"], 3);
        }

        [Fact]
        public void A_clock_set_back_doesnt_strengthen_cached_rates()
        {
            var cached = new MarketRates { market = "0", categories = new Dictionary<string, float> { ["FOOD"] = 1.5f }, saved_at = 10_000 };
            Assert.Equal(1.5f, Pricing.FadedRates(cached, 0)["FOOD"], 3);
        }

        [Fact]
        public void Broken_cached_rates_are_dropped()
        {
            var cached = new MarketRates
            {
                market = "0",
                categories = new Dictionary<string, float> { ["A"] = 0f, ["B"] = -1f, ["C"] = float.PositiveInfinity, ["D"] = 1.1f },
                saved_at = 0
            };
            Assert.Equal(new[] { "D" }, Pricing.FadedRates(cached, 0).Keys);
        }

        [Fact]
        public void Payout_line_numbers_add_up()
        {
            PendingBatch batch = Batch(("a", 300, new string[0]), ("b", 200, new string[0]));
            Assert.Equal((500L, 40L), Pricing.Compare(batch, 540));
            Assert.Equal((500L, -100L), Pricing.Compare(batch, 400));
            Assert.Equal((500L, 0L), Pricing.Compare(batch, 500));
        }

        [Theory]
        [InlineData(1f, 0)]
        [InlineData(1.104f, 10)]
        [InlineData(1.106f, 11)]
        [InlineData(0.8f, -20)]
        public void Percent_lines_match_the_multiplier(float multiplier, int percent)
        {
            Assert.Equal(percent, Pricing.Percent(multiplier));
        }

        [Fact]
        public void Category_rate_is_missing_when_no_category_is_priced()
        {
            Assert.Null(Pricing.CategoryRate(new[] { "FOOD" }, new Dictionary<string, float> { ["DRUG"] = 1.2f }));
            Assert.Null(Pricing.CategoryRate(new[] { "FOOD" }, null));
            Assert.Equal(1.1f, Pricing.CategoryRate(new[] { "FOOD", null, "X" }, new Dictionary<string, float> { ["FOOD"] = 1.1f }));
        }
    }

    public class RatesCacheTests
    {
        [Fact]
        public void Saved_rates_read_back()
        {
            var rates = new MarketRates { market = "abc", categories = new Dictionary<string, float> { ["FOOD"] = 1.3f }, fee = 0.1f, saved_at = 42 };
            MarketRates read = RatesCache.Parse(JsonConvert.SerializeObject(rates));
            Assert.Equal("abc", read.market);
            Assert.Equal(1.3f, read.categories["FOOD"]);
            Assert.Equal(0.1f, read.fee);
            Assert.Equal(42, read.saved_at);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("{not json")]
        [InlineData("[1,2,3]")]
        public void Damaged_file_means_no_cache(string json)
        {
            Assert.Null(RatesCache.ForMarket(RatesCache.Parse(json), "0"));
        }

        [Fact]
        public void Rates_of_another_market_or_without_categories_are_ignored()
        {
            var rates = new MarketRates { market = "a", categories = new Dictionary<string, float>() };
            Assert.Null(RatesCache.ForMarket(rates, "b"));
            Assert.Same(rates, RatesCache.ForMarket(rates, "a"));
            Assert.Null(RatesCache.ForMarket(new MarketRates { market = "a" }, "a"));
        }
    }

    public class FailedDaysTests
    {
        [Fact]
        public void First_failure_is_day_one_and_retrying_the_same_day_doesnt_count_again()
        {
            Assert.Equal(1, FailedDays.Count(null, 5, out string store));
            Assert.Equal("1|5", store);
            Assert.Equal(1, FailedDays.Count(store, 5, out string again));
            Assert.Null(again);
        }

        [Fact]
        public void A_second_day_lets_the_trader_come_without_the_server()
        {
            FailedDays.Count(null, 5, out string store);
            Assert.Equal(2, FailedDays.Count(store, 6, out store));
            Assert.Equal("2|6", store);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("0|5")]
        [InlineData("-3|5")]
        [InlineData("x|5")]
        public void A_damaged_record_starts_over(string stored)
        {
            Assert.Equal(1, FailedDays.Count(stored, 5, out _));
        }
    }

    public class CallTests
    {
        [Fact]
        public void Most_calls_say_tomorrow_some_two_or_three_days()
        {
            int[] delays = Enumerable.Range(0, Calls.RollRange).Select(Calls.Delay).ToArray();
            Assert.Equal(6, delays.Count(d => d == 1));
            Assert.Equal(3, delays.Count(d => d == 2));
            Assert.Equal(1, delays.Count(d => d == 3));
        }

        [Fact]
        public void A_call_reads_back()
        {
            Assert.True(Calls.TryParse(Calls.Format(12, "FOOD"), out int day, out string category));
            Assert.Equal(12, day);
            Assert.Equal("FOOD", category);
            Assert.True(Calls.TryParse(Calls.Format(3, ""), out _, out string any));
            Assert.Equal("", any);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("x|FOOD")]
        [InlineData("1|2|3")]
        public void A_damaged_call_is_ignored(string call)
        {
            Assert.False(Calls.TryParse(call, out _, out _));
        }
    }

    public class RollTests
    {
        private const string Install = "0123456789abcdef0123456789abcdef";

        [Fact]
        public void The_same_install_and_day_always_roll_the_same()
        {
            Assert.Equal(Rolls.Roll(Install, 17, 10), Rolls.Roll(Install, 17, 10));
        }

        [Fact]
        public void Rolls_stay_in_range_and_spread_out()
        {
            int[] rolls = Enumerable.Range(0, 2000).Select(day => Rolls.Roll(Install, day, 10)).ToArray();
            Assert.All(rolls, roll => Assert.InRange(roll, 0, 9));
            Assert.Equal(10, rolls.Distinct().Count());
            // The trader's miss chance is 10%; over many items it should land near that.
            double missed = Enumerable.Range(0, 5000).Count(id => Rolls.Roll(Install, id, 100) < 10) / 5000.0;
            Assert.InRange(missed, 0.07, 0.13);
        }

        [Fact]
        public void Negative_keys_are_fine()
        {
            Assert.InRange(Rolls.Roll(Install, -123456, 100), 0, 99);
        }
    }

    public class IdentityTests
    {
        [Theory]
        [InlineData("0123456789abcdef0123456789abcdef", true)]
        [InlineData("not-an-id", false)]
        [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
        [InlineData("0123456789abcdef0123456789abcde", false)]
        [InlineData(null, false)]
        public void Client_ids_must_be_32_lowercase_hex(string id, bool valid)
        {
            Assert.Equal(valid, Identity.IsValidClientId(id));
        }

        [Fact]
        public void A_new_guid_is_a_valid_client_id()
        {
            Assert.True(Identity.IsValidClientId(Guid.NewGuid().ToString("N")));
        }

        [Theory]
        [InlineData("", "0")]
        [InlineData(null, "0")]
        [InlineData("  My Market ", "mymarket")]
        [InlineData("a_b-c!?", "a_b-c")]
        [InlineData("!!!", "0")]
        public void Market_codes_are_cleaned(string setting, string code)
        {
            Assert.Equal(code, Identity.MarketCode(setting));
        }

        [Fact]
        public void Market_codes_are_cut_to_32_characters()
        {
            Assert.Equal(new string('a', 32), Identity.MarketCode(new string('a', 50)));
        }

        [Theory]
        [InlineData("<color=red>Bob</color>", "color=redBob/color")]
        [InlineData("  Alice\n ", "Alice")]
        [InlineData(null, "")]
        public void Seller_names_lose_markup(string sent, string shown)
        {
            Assert.Equal(shown, Identity.CleanSellerName(sent));
        }

        [Fact]
        public void Seller_names_are_cut_to_24_characters()
        {
            Assert.Equal(24, Identity.CleanSellerName(new string('x', 60)).Length);
        }

        [Theory]
        [InlineData("Large Fish", "Fish", true)]
        [InlineData("Cigarettes (Counterfeit)", "Cigarettes", true)]
        [InlineData("Totally Different", "Fish", false)]
        [InlineData("<b>Fish</b>", "Fish", false)]
        [InlineData("", "Fish", false)]
        public void Only_names_built_around_the_item_survive(string sent, string local, bool kept)
        {
            Assert.Equal(kept, Identity.IsPlausibleName(sent, local));
        }

        [Fact]
        public void A_name_padded_far_past_the_item_is_replaced()
        {
            Assert.False(Identity.IsPlausibleName("Fish" + new string('!', 40), "Fish"));
        }
    }

    public class LevelTests
    {
        [Theory]
        [InlineData(0, 4)]
        [InlineData(2499, 4)]
        [InlineData(2500, 6)]
        [InlineData(7500, 8)]
        [InlineData(20000, 10)]
        [InlineData(1_000_000, 12)]
        public void A_new_save_gets_four_and_more_trade_brings_more(long credits, int offers)
        {
            Assert.Equal(offers, Levels.At(credits).Offers);
        }

        [Fact]
        public void Reaching_2500_announces_a_level_up()
        {
            Assert.Equal(6, Levels.UpAfter(2000, 500));
            Assert.Null(Levels.UpAfter(2000, 499));
            Assert.Null(Levels.UpAfter(2500, 100));
            Assert.Null(Levels.UpAfter(2000, -5000));
        }

        [Fact]
        public void A_jump_past_several_levels_names_the_highest()
        {
            Assert.Equal(10, Levels.UpAfter(0, 25000));
        }
    }

    public class EnvelopeTests
    {
        [Fact]
        public void Marks_survive_the_envelope()
        {
            ItemEnvelope sent = new ItemEnvelope
            {
                lang = "en",
                seller = "Ann",
                state = "{\"saveItems\":[]}",
                marks = new List<ItemMark> { new ItemMark { node = 1, category = "CATEGORY_GENUINE_STAMP", shown = "genuineStamp", real = "counterfeitStamp" } }
            };
            ItemEnvelope read = Envelopes.Read(Envelopes.Write(sent));
            Assert.Equal(sent.state, read.state);
            ItemMark mark = Assert.Single(read.marks);
            Assert.Equal((1, "CATEGORY_GENUINE_STAMP", "genuineStamp", "counterfeitStamp"), (mark.node, mark.category, mark.shown, mark.real));
        }

        [Fact]
        public void A_payload_written_before_marks_existed_still_reads()
        {
            ItemEnvelope read = Envelopes.Read("{\"lang\":\"en\",\"seller\":\"Ann\",\"state\":\"x\"}");
            Assert.Equal("x", read.state);
            Assert.Null(read.marks);
        }

        [Fact]
        public void Text_that_isnt_JSON_throws()
        {
            Assert.ThrowsAny<JsonException>(() => Envelopes.Read("{not json"));
        }
    }

    public class PurchasesTests
    {
        [Fact]
        public void Empty_and_blank_ids_are_dropped()
        {
            Assert.Equal(new[] { "a", "b" }, Purchases.Parse("[\"a\",\"\",null,\"b\"]"));
            Assert.Empty(Purchases.Parse(null));
            Assert.Empty(Purchases.Parse(""));
        }
    }

    public class AssortmentTests
    {
        private class Entry
        {
            public string Id;
            public long Value;
        }

        private static Entry[] Entries(params long[] values) => values.Select((v, i) => new Entry { Id = "i" + i, Value = v }).ToArray();

        [Fact]
        public void Minted_items_cost_about_the_same_as_the_sold_item()
        {
            Entry[] entries = Entries(10, 95, 110, 400);
            for (int pick = 0; pick < 2; pick++)
            {
                Entry chosen = Assortment.Pick(entries, e => e.Value, 100, n => pick % n);
                Assert.InRange(chosen.Value, 75, 125);
            }
        }

        [Fact]
        public void Without_a_close_item_the_nearest_within_double_is_used()
        {
            Assert.Equal(160, Assortment.Pick(Entries(10, 160, 500), e => e.Value, 100, n => 0).Value);
        }

        [Fact]
        public void Nothing_is_minted_when_no_item_is_within_double()
        {
            Assert.Null(Assortment.Pick(Entries(10, 300), e => e.Value, 100, n => 0));
            Assert.Null(Assortment.Pick(Entries(100), e => e.Value, 0, n => 0));
            Assert.Null(Assortment.Pick(Entries(), e => e.Value, 100, n => 0));
        }

        [Fact]
        public void An_item_without_types_is_matched_on_price_alone()
        {
            var entries = new[] { (Id: "a", Types: new[] { "FOOD" }), (Id: "b", Types: new string[0]) };
            Assert.Equal(2, Assortment.Matching(entries, e => e.Types, new List<string>()).Count());
            Assert.Equal(new[] { "a" }, Assortment.Matching(entries, e => e.Types, new List<string> { "FOOD" }).Select(e => e.Id));
        }

        private static readonly string[] Major = { "FOOD", "WEAPON", "TOOL" };

        [Fact]
        public void The_asked_category_decides_what_comes_back()
        {
            Assert.Equal(new[] { "TOOL" }, Assortment.WantedTypes(new List<string> { "FOOD" }, "TOOL", Major));
        }

        [Fact]
        public void Without_a_category_minor_types_dont_decide()
        {
            Assert.Equal(new[] { "FOOD" }, Assortment.WantedTypes(new List<string> { "SHINY", "FOOD" }, "", Major));
            Assert.Equal(new[] { "SHINY" }, Assortment.WantedTypes(new List<string> { "SHINY" }, null, Major));
            Assert.Empty(Assortment.WantedTypes(null, null, Major));
        }

        [Fact]
        public void Sale_types_put_major_ones_first_and_keep_five()
        {
            var types = new[] { "a", "FOOD", "b", "TOOL", "c", "d", "FOOD" };
            Assert.Equal(new[] { "FOOD", "TOOL", "a", "b", "c" }, Assortment.SaleTypes(types, Major, 5));
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TheFlea
{
    internal static class TraderVisit
    {
        public const string ClientId = "flea_trader_visit";
        // The server answers 400 to a request it will never accept.
        private const long RefusedStatus = 400;
        private const int MaxSaleTypes = 5;
        private const string MarketPriceType = "MATERIAL";
        private const string MarketPriceId = "flea_market_price";
        private const string MarketFeeId = "flea_market_fee";
        private const string MarketItemId = "flea_market_item";
        private const string LocalPrefix = "local:";
        private const int MaxReportedPurchases = 30;

        private static readonly Dictionary<GameItem, int> marketPrices = new Dictionary<GameItem, int>();
        private static Dictionary<string, float> categoryPrices;
        private static float marketFee;
        private const float VisitTimeoutSeconds = 120f;
        private const float StoreOpenWaitSeconds = 1f;
        private static float busyUntil;

        public static bool IsCurrentClient(PlayerStore store)
        {
            return store.currentClientInstance?.GetClientBlueprint()?.identifier == ClientId;
        }

        public static bool IsTraderPresent()
        {
            // The price patches run on every item's value, so an error here must never escape.
            try
            {
                return PlayerStore.Instance != null && IsCurrentClient(PlayerStore.Instance);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static long MarketPercent(GameItem item)
        {
            return marketPrices.TryGetValue(item, out int percent) ? percent : 0;
        }

        public static void OnCustomersGenerated(StoreClientManager manager)
        {
            // Times out rather than being reset by the visit, because a visit that hits an error never resumes.
            if (UnityEngine.Time.realtimeSinceStartup >= busyUntil && BatchStore.TryGetDueCall(out string category))
            {
                busyUntil = UnityEngine.Time.realtimeSinceStartup + VisitTimeoutSeconds;
                ModEntry.Host.StartCoroutine(Run(category));
            }
        }

        private static IEnumerator Run(string category)
        {
            string client = BatchStore.ClientId();
            string market = ModEntry.MarketCode();
            string error = null;
            long payout = 0;
            bool priced = false;

            // A batch settles in the market it was sold to, even if the code has changed since.
            PendingBatch batch = BatchStore.Load();
            if (batch != null)
            {
                SalesRequest request = new SalesRequest { market = batch.market, client = client, batch_id = batch.batch_id, items = batch.items };
                string failure = null;
                long status = 0;
                yield return MarketClient.Post<SalesResponse>("/v1/sales", request,
                    response => { payout = GrossSale(batch, response); priced = true; }, (e, code) => { failure = e; status = code; });
                // A refused batch would be refused every day, so it's paid locally once the offers bring fresh rates.
                // The server didn't record it, and PayOut clears the batch, so nothing can pay it again.
                // Other failures may pass later and are retried.
                if (failure != null && status == RefusedStatus)
                {
                    ModEntry.Log.Warning("Market refused the batch, paying it locally from market rates: " + failure);
                }
                else if (failure != null)
                {
                    error = failure;
                }
            }

            List<Offer> offers = new List<Offer>();
            MarketRates fresh = null;
            if (error == null)
            {
                // Offers are taken off the market as they're sent, so they're only asked for a visit that can happen.
                // Without a batch nothing has waited yet, and the store may not count as open in the very frame
                // its customers are generated, so this waits a moment before looking.
                yield return new UnityEngine.WaitForSeconds(StoreOpenWaitSeconds);
                if (PlayerStore.Instance.storeState != PlayerStore.StoreState.OPEN)
                {
                    busyUntil = 0f;
                    ModEntry.Log.Warning("The store isn't open, so the trader waits for another day.");
                    yield break;
                }
                List<string> bought = BatchStore.UnreportedPurchases().Take(MaxReportedPurchases).ToList();
                OffersRequest request = new OffersRequest
                {
                    market = market,
                    client = client,
                    category = category,
                    limit = TraderLevel.Offers(),
                    categories = TypeHelper.MAJOR_TYPES_LIST.ToList(),
                    bought = bought
                };
                string failure = null;
                long status = 0;
                yield return MarketClient.Post<OffersResponse>("/v1/offers", request, response =>
                {
                    offers = (response.offers ?? new List<Offer>())
                        .Where(offer => offer != null && !string.IsNullOrEmpty(offer.listing_id) && !IsLocal(offer.listing_id))
                        .Take(TraderLevel.Offers())
                        .ToList();
                    categoryPrices = response.categories;
                    marketFee = SafeFee(response.fee);
                    fresh = BatchStore.SaveRates(market, response.categories, marketFee);
                }, (e, code) => { failure = e; status = code; });
                if (failure == null)
                {
                    BatchStore.RemovePurchases(bought);
                }
                // A refused request would be refused every day, so the visit goes ahead with locally minted offers only.
                // Its purchase report would be refused again too, so it's dropped.
                else if (status == RefusedStatus)
                {
                    ModEntry.Log.Warning("Market refused the offers request, the trader brings local items only: " + failure);
                    BatchStore.RemovePurchases(bought);
                    // Prices from an earlier visit this session would be stale, so local offers go at their plain value.
                    categoryPrices = null;
                    marketFee = 0f;
                }
                else
                {
                    error = failure;
                }
            }

            // The first failed day waits for the server. From the second, the trader comes without it, so a server
            // that's gone for good doesn't keep him away. Unreported purchases wait for the next answered request.
            if (error != null)
            {
                int failedDays = BatchStore.CountFailedDay();
                if (failedDays < 2)
                {
                    busyUntil = 0f;
                    ModEntry.Log.Warning("Market request failed, trying again tomorrow: " + error);
                    yield break;
                }
                ModEntry.Log.Warning($"Market request failed on {failedDays} days in a row, the trader comes with local items and cached rates: {error}");
                offers.Clear();
                MarketRates cached = BatchStore.LoadRates(market);
                categoryPrices = cached != null ? FadedRates(cached) : null;
                marketFee = cached != null ? SafeFee(cached.fee) : 0f;
            }
            else
            {
                BatchStore.ClearFailedDays();
            }

            // A batch the server didn't price is paid from the market's rates: the offers' own if they just came
            // from the same market, otherwise the faded cache.
            if (batch != null && !priced)
            {
                payout = fresh != null && fresh.market == batch.market ? LocalSale(batch, fresh.categories, fresh.fee) : CachedSale(batch);
            }

            yield return MintCatalogue.Load();
            AddLocalOffers(offers, category);

            busyUntil = 0f;
            if (PlayerStore.Instance.storeState != PlayerStore.StoreState.OPEN)
            {
                yield break;
            }
            try
            {
                Summon(category, offers, batch, payout);
            }
            catch (Exception e)
            {
                ModEntry.Log.Error("Could not summon the trader, trying again tomorrow: " + e);
            }
        }

        // The server sends fewer offers than the trader brings when the market has few listings, such as
        // when you're its only seller. The game mints the rest itself. The server never offered these, so
        // they're left out of the purchase report and don't move prices.
        private static void AddLocalOffers(List<Offer> offers, string category)
        {
            int missing = TraderLevel.Offers() - offers.Count;
            if (missing <= 0 || !MintCatalogue.HasAny(category))
            {
                return;
            }
            for (int index = 0; index < missing; index++)
            {
                offers.Add(new Offer { listing_id = LocalPrefix + index, item_id = "", payload = "", minted = true, types = new List<string>() });
            }
        }

        // Only real listings are reported. Minted offers were never stored, so buying them doesn't move prices.
        private static List<string> RealIds(IEnumerable<Offer> offers)
        {
            return offers.Where(offer => !offer.minted).Select(offer => offer.listing_id).ToList();
        }

        private static bool IsLocal(string listingId)
        {
            return listingId.StartsWith(LocalPrefix, StringComparison.Ordinal);
        }

        // Follows the market's category prices, the same as a minted offer from the server.
        private static float LocalMultiplier(GameItem item)
        {
            float? rate = Pricing.CategoryRate(item.GetGameItemType(), categoryPrices);
            return rate != null ? SafeMultiplier(rate.Value, 1f) : 1f;
        }

        private static float SafeMultiplier(float multiplier, float fallback) => Pricing.SafeMultiplier(multiplier, fallback);

        private static float SafeFee(float fee) => Pricing.SafeFee(fee);

        private static Dictionary<string, float> FadedRates(MarketRates cached) => Pricing.FadedRates(cached, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Pays a batch the server didn't price from the cached rates of its market, or at the flat rate without them.
        private static long CachedSale(PendingBatch batch)
        {
            MarketRates cached = BatchStore.LoadRates(batch.market);
            return cached != null ? LocalSale(batch, FadedRates(cached), cached.fee) : GrossSale(batch, new SalesResponse());
        }

        private static long LocalSale(PendingBatch batch, Dictionary<string, float> rates, float fee) => Pricing.LocalSale(batch, rates, fee);

        private static long GrossSale(PendingBatch batch, SalesResponse response) => Pricing.GrossSale(batch, response);

        // Compares the payout with what the batch was worth when it was handed over.
        private static string PaidLine(PendingBatch batch, long payout)
        {
            (long worth, long difference) = Pricing.Compare(batch, payout);
            if (difference == 0)
            {
                return ModEntry.Line("visit_paid_even", payout);
            }
            return ModEntry.Line(difference > 0 ? "visit_paid_over" : "visit_paid_under", payout, Math.Abs(difference), worth);
        }

        private static void Summon(string category, List<Offer> offers, PendingBatch batch, long payout)
        {
            string name = ModEntry.L("trader_name");
            Dictionary<GameItem, string> stock = new Dictionary<GameItem, string>();
            HashSet<string> real = new HashSet<string>(RealIds(offers));

            StoreClient client = new StoreClient
            {
                identifier = ClientId,
                displayName = name,
                spriteName = SpriteDict.GetRandomShadySpriteName(),
                clientIntent = StoreClient.ClientIntent.SELLNBUY,
                isIntroduced = true,
                isBargainAvailable = false,
                acceptRetailMarkup = false,
                AcceptingContrabandOverride = true,
                tradeReputationMultiplier = 0.0,
                isNoContributeToEvidence = true,
                CanReturn = false,
                CanReturnAsBuyer = false,
                cooldown = 0
            };
            client.AddBasicDialog();
            client.CompleteClientCreation(IgnoreExposeFunc: true);
            client.willAcceptItem = delegate(GameItem item)
            {
                string refusal = Refusal(item);
                if (refusal != null)
                {
                    client.placedWrongItemWhenSellingToDialogue = new Dialogue().SetText(name, refusal);
                }
                return refusal == null;
            };
            client.canClientExposeFeature = (feature, _) => FakeCheck.WillCatch(feature);
            client.onItemSold = delegate(GameItem item, int cost)
            {
                if (stock.TryGetValue(item, out string listingId))
                {
                    marketPrices.Remove(item);
                    item.RemoveFeatureRelatedToEvent();
                    // Reported with the next offers request. A stand-in counts as buying the listing it replaced.
                    if (real.Contains(listingId))
                    {
                        BatchStore.AddPurchase(listingId);
                    }
                    string levelUp = TraderLevel.LevelUpLineAfter(cost);
                    TraderLevel.Add(cost);
                    if (levelUp != null)
                    {
                        Dialogue line = new Dialogue().SetText(name, levelUp);
                        line.priority = Dialogue.IMPORTANT_PRIORITY;
                        DialogUIManager.Instance.StartDialogue(line);
                    }
                }
            };
            client.OnDismiss = delegate
            {
                marketPrices.Clear();
                try
                {
                    RemoveMarketLines();
                }
                catch (Exception e)
                {
                    PatchErrors.LogOnce(nameof(RemoveMarketLines), e);
                }
            };

            Dialogue lastLine = client.mainDialogue;
            if (batch != null)
            {
                lastLine.SetText(name, PaidLine(batch, payout)).SetStartAction(delegate
                {
                    PayOut(batch.batch_id, payout);
                });
                lastLine = lastLine.NextDialogue();
                string levelUp = TraderLevel.LevelUpLineAfter(payout);
                if (levelUp != null)
                {
                    lastLine.SetText(name, levelUp);
                    lastLine = lastLine.NextDialogue();
                }
            }
            lastLine.SetText(name, ModEntry.Line(offers.Count > 0 ? "visit_offers" : "visit_empty")).SetStartAction(BatchStore.ClearCall).SetEndAction(delegate
            {
                PlaceOffers(offers, category, stock);
            });

            ModHelper.SetClientSource(client, ModEntry.ModId);
            PlayerStore.Instance.storeClientManager.AddClient(client);
        }

        // The batch stays unpaid until this runs, so a trader who never arrives loses nothing:
        // the next call resends the same batch ID and the server repeats its earlier answer.
        private static void PayOut(string batchId, long payout)
        {
            if (BatchStore.Load()?.batch_id == batchId)
            {
                BatchStore.Clear();
                ModHelper.AddCredit((int)payout);
                TraderLevel.Add(payout);
            }
        }

        private static void PlaceOffers(List<Offer> offers, string category, Dictionary<GameItem, string> stock)
        {
            foreach (Offer offer in offers)
            {
                try
                {
                    // A listing this game doesn't have the items for becomes a local item of similar value.
                    bool local = IsLocal(offer.listing_id);
                    GameItem item = offer.minted ? null : ItemCodec.Decode(offer.payload, offer.item_id);
                    if (item == null)
                    {
                        item = local ? MintCatalogue.CreateAny(category) : MintCatalogue.Create(offer, category);
                    }
                    if (item == null)
                    {
                        continue;
                    }
                    if (PlayerStore.Instance.TryAddDirectSellingItemToTable(item, isOwend: false, isStolen: false, ignorePerk: true))
                    {
                        float multiplier = local ? LocalMultiplier(item) : SafeMultiplier(offer.buy_multiplier, 1f);
                        marketPrices[item] = Pricing.Percent(multiplier);
                        ReplaceEventPrices(item);
                        stock[item] = offer.listing_id;
                    }
                }
                catch (Exception e)
                {
                    ModEntry.Log.Warning($"Skipped offer {offer.listing_id}: {e}");
                }
            }
        }

        // The market's price already reflects supply and demand, so the trader ignores the game's
        // events, which would otherwise add up to extreme prices. Perks are separate features and stay.
        // The market line uses the event price line's look, and like one it applies only until bought.
        public static void ReplaceEventPrices(GameItem item)
        {
            if (!IsTraderPresent())
            {
                return;
            }
            item.RemoveFeatureRelatedToEvent();
            if (marketPrices.TryGetValue(item, out int percent))
            {
                // Buyers pay the trader's cut on top, the same share sellers give up.
                AddMarketLine(item, MarketPriceId, percent, ModEntry.L("feature_market_price"));
                AddMarketLine(item, MarketFeeId, UnityEngine.Mathf.RoundToInt(marketFee * 100f), ModEntry.L("feature_market_fee"));
            }
            else if (GeneralHelper.IsItemOwned(item))
            {
                AddConsignLines(item);
            }
        }

        // An estimate of what the item may fetch, so the tablet's price follows these lines.
        // They're event lines, so Consign drops them before recording the item's value.
        private static void AddConsignLines(GameItem item)
        {
            float? rate = Pricing.CategoryRate(item.GetGameItemType(), categoryPrices);
            if (rate != null)
            {
                AddMarketLine(item, MarketPriceId, Pricing.Percent(rate.Value), ModEntry.L("feature_market_category"));
            }
            AddMarketLine(item, MarketFeeId, -UnityEngine.Mathf.RoundToInt(marketFee * 100f), ModEntry.L("feature_market_fee"));
            AddMarketLine(item, MarketItemId, 0, ModEntry.L("feature_market_item"), showZero: true);
        }

        private static void AddMarketLine(GameItem item, string id, int percent, string display, bool showZero = false)
        {
            if (percent != 0 || showZero)
            {
                NegociationData price = new NegociationData(MarketPriceType, percent, "", "") { identifier = id };
                item.AddItemFeature(ItemFeature.CreateFeatureFromNegociationData(price).SetPublicDisplay(display));
            }
        }

        // Anything left on the counter shouldn't carry the trader's lines to the next customer.
        private static void RemoveMarketLines()
        {
            foreach (GameItem item in EmporiumEntry.Instance.backInvinvElement.childItems.Concat(EmporiumEntry.Instance.frontInvinvElement.childItems))
            {
                item.RemoveItemFeatureByID(MarketPriceId);
                item.RemoveItemFeatureByID(MarketFeeId);
                item.RemoveItemFeatureByID(MarketItemId);
            }
        }

        // What the trader says when he won't take an item, or null if he will.
        private static string Refusal(GameItem item)
        {
            if (ItemCodec.HasHotPart(item))
            {
                return ModEntry.Line("visit_hot");
            }
            if (ItemCodec.HasContrabandPart(item))
            {
                return ModEntry.Line("visit_contraband");
            }
            if (!GeneralHelper.IsItemOwned(item) || !ItemCodec.CanEncode(item) || ItemCodec.IsWorthless(item))
            {
                return ModEntry.Line("visit_wrong");
            }
            return BatchStore.IsFull() ? ModEntry.Line("visit_full", TraderLevel.Items()) : null;
        }

        // Replaces PlayerStore.SellItem while the trader is at the counter: no cash now, paid next visit.
        public static void Consign(GameItem item)
        {
            item.CloseContentWindow(includeChildren: true);
            // Refusal checks the cap only when an item is placed, so several items on the counter could pass it.
            // The server refuses an oversized batch, so the item stays on the counter.
            if (BatchStore.IsFull())
            {
                Dialogue full = new Dialogue().SetText(ModEntry.L("trader_name"), ModEntry.Line("visit_full", TraderLevel.Items()));
                full.priority = Dialogue.IMPORTANT_PRIORITY;
                DialogUIManager.Instance.StartDialogue(full);
                return;
            }
            FakeCheck.InspectAll(PlayerStore.Instance.currentClientInstance.GetClientBlueprint(), item);
            item.RemoveFeatureRelatedToEvent();
            long value = item.GetNegociatedValue();
            // Defects can take an item to nothing after the trader accepted it, so it's refused here too and stays on the counter.
            if (value <= 0)
            {
                Dialogue refusal = new Dialogue().SetText(ModEntry.L("trader_name"), ModEntry.Line("visit_wrong"));
                refusal.priority = Dialogue.IMPORTANT_PRIORITY;
                DialogUIManager.Instance.StartDialogue(refusal);
                return;
            }
            PendingItem pending = new PendingItem
            {
                item_id = item.identifier,
                count = item.unitCount,
                types = SaleTypes(item),
                payload = ItemCodec.Encode(item),
                value = value
            };
            BatchStore.Add(pending);
            try
            {
                GeneralHelper.SetItemOwned(item, own: false);
                GraphUtils.TryAcceptAll(EmporiumEntry.Instance.soldElement, item);
                item.Destroy();
            }
            catch (Exception)
            {
                // The item is still here, so it mustn't also be paid for.
                BatchStore.RemoveItem(pending);
                GeneralHelper.SetItemOwned(item, own: true);
                throw;
            }
        }

        // The server keeps five categories, so the ones the trader can be asked for go first.
        private static List<string> SaleTypes(GameItem item)
        {
            return Assortment.SaleTypes(item.GetGameItemType(), TypeHelper.MAJOR_TYPES_LIST, MaxSaleTypes);
        }
    }
}

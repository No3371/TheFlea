using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TheFlea
{
    // F9 > Modding > "The Flea: run self-tests". Checks the parts of TESTING.md that need the game's items
    // but not a trader visit: item round trips, forged listings, fake marks and minted offers.
    // Results go to the F8 console and Player.log. Decoding creates and destroys items and uses up
    // unique IDs, so it's meant for a test save.
    internal static class SelfTest
    {
        private const string Prefix = "[Self-test] ";
        private const float SecondsPerFrame = 0.008f;
        private const int MaxLoggedCatalogueFailures = 30;
        private const int MintSamples = 40;

        private const string CigaretteId = "packet_red_cigarette";
        private const string StampId = "food_stamp";
        private const string InjectorId = "large_purple_injector";

        private static bool running;
        private static int passed;
        private static int failed;
        private static int skipped;

        public static void Register()
        {
            ModHelper.AddDebugFunction(ModEntry.ModId, ModHelper.DEBUG_CATEGORY_MODDING, "The Flea: run self-tests", Start);
        }

        public static void Unregister()
        {
            ModHelper.RemoveDebugFunctions(ModEntry.ModId);
        }

        private static void Start()
        {
            if (running)
            {
                ModEntry.Log.Log(Prefix + "Already running.");
                return;
            }
            if (PlayerStore.Instance == null || ModEntry.Host == null)
            {
                ModEntry.Log.Warning(Prefix + "Load a save first.");
                return;
            }
            ModEntry.Host.StartCoroutine(Run());
        }

        // Things found while scanning the catalogue, used by the later sections.
        private class Samples
        {
            public GameItem Single;
            public GameItem Container;
            public string BlockedId;
            public readonly List<(string Id, long Value, List<string> Types)> Mintable = new List<(string, long, List<string>)>();
            public int RoundTrips;
            public int RoundTripFailures;
            public int RefusalFailures;
            // Items worth preparing with a state a fresh one doesn't have.
            public readonly List<string> Liquids = new List<string>();
            public readonly List<string> Batteries = new List<string>();
            public readonly List<string> Stacks = new List<string>();
            public readonly List<string> Guns = new List<string>();
            public readonly List<string> Bags = new List<string>();
        }

        private static IEnumerator Run()
        {
            running = true;
            passed = failed = skipped = 0;
            Samples samples = new Samples();
            ModEntry.Log.Log(Prefix + "Started. Use a test save: decoding creates items and uses up unique IDs.");
            try
            {
                List<string> ids = Identifiers();
                float frameStart = Time.realtimeSinceStartup;
                foreach (string id in ids)
                {
                    ScanCatalogueItem(id, samples);
                    if (Time.realtimeSinceStartup - frameStart >= SecondsPerFrame)
                    {
                        yield return null;
                        frameStart = Time.realtimeSinceStartup;
                    }
                }
                Report("Every encodable item survives selling and buying back unchanged", samples.RoundTripFailures == 0,
                    $"{samples.RoundTripFailures} of {samples.RoundTrips} items changed (first {MaxLoggedCatalogueFailures} listed above)");
                Report("Machines, not-for-resale items, access cards and contraband can't be sold", samples.RefusalFailures == 0,
                    $"{samples.RefusalFailures} blocked items were accepted (listed above)");

                yield return CheckPreparedItems(samples);
                CheckCounterItems();
                yield return null;
                CheckForgedListings(samples);
                yield return null;
                CheckFakeMarks();
                yield return null;
                yield return MintCatalogue.Load();
                CheckMintedOffers(samples);
            }
            finally
            {
                samples.Single?.Destroy();
                samples.Container?.Destroy();
                running = false;
            }
            string summary = $"{Prefix}Finished: {passed} passed, {failed} failed, {skipped} skipped.";
            if (failed > 0)
            {
                ModEntry.Log.Warning(summary);
            }
            else
            {
                ModEntry.Log.Log(summary);
            }
        }

        private static List<string> Identifiers()
        {
            try
            {
                return DirectoryMaster.GetIdentifierList<GameItem>();
            }
            catch (Exception e)
            {
                Fail("List the game's items", e.Message);
                return new List<string>();
            }
        }

        // ---- Catalogue: every item once, fresh from the game's definitions.

        private static void ScanCatalogueItem(string id, Samples samples)
        {
            GameItem item = null;
            bool keep = false;
            try
            {
                if (ItemSpawnerHiddenList.IsHidden(id))
                {
                    return;
                }
                item = DirectoryMaster.Item(id, isOwned: false);
                bool shouldRefuse = item.HaveType("MACHINE") || item.HaveType(TypeHelper.NOT_FOR_RESALE) || item.HaveType("ACCESS_CARD")
                    || ContrabandHelper.IsContraband(item);
                if (shouldRefuse)
                {
                    samples.BlockedId = samples.BlockedId ?? (item is GameCharacterItem ? null : id);
                    if (ItemCodec.CanEncode(item))
                    {
                        samples.RefusalFailures++;
                        ModEntry.Log.Warning($"{Prefix}FAIL {id} is blocked but could be sold");
                    }
                    return;
                }
                if (!ItemCodec.CanEncode(item) || ItemCodec.IsWorthless(item))
                {
                    return;
                }
                samples.Mintable.Add((id, item.GetBaseValue(), item.GetGameItemType().ToList()));
                Classify(id, item, samples);
                samples.RoundTrips++;
                string problem = RoundTrip(item);
                if (problem != null)
                {
                    samples.RoundTripFailures++;
                    if (samples.RoundTripFailures <= MaxLoggedCatalogueFailures)
                    {
                        ModEntry.Log.Warning($"{Prefix}FAIL round trip {id}: {problem}");
                    }
                    return;
                }
                bool hasChildren = item.FindAllChildItems().Count > 0;
                if (samples.Container == null && hasChildren)
                {
                    samples.Container = item;
                    keep = true;
                }
                else if (samples.Single == null && !hasChildren && item.maxSlotAmountItemFunc == null)
                {
                    samples.Single = item;
                    keep = true;
                }
            }
            catch (Exception e)
            {
                samples.RoundTripFailures++;
                if (samples.RoundTripFailures <= MaxLoggedCatalogueFailures)
                {
                    ModEntry.Log.Warning($"{Prefix}FAIL round trip {id} threw: {e.GetType().Name}: {e.Message}");
                }
            }
            finally
            {
                if (!keep)
                {
                    item?.Destroy();
                }
            }
        }

        // ---- Prepared items: built in code with a state a fresh item doesn't have.

        private const int SamplesPerKind = 2;
        private const int StackSamples = 4;
        private const int StackCount = 7;
        private const int PartCount = 3;
        private const int MaxPartsPerBag = 3;

        private static void Classify(string id, GameItem item, Samples samples)
        {
            if (item.IsTag("LIQUID_CONTAINER_TAG"))
            {
                AddSample(samples.Liquids, id, SamplesPerKind);
            }
            // Energy credits: PowerHelper.InitPowerSourceItem.
            if (item.IsTag("power_source_item"))
            {
                AddSample(samples.Batteries, id, SamplesPerKind);
            }
            if (item.FindAllChildItems().Count == 0 && StackLimit(item) >= StackCount)
            {
                AddSample(samples.Stacks, id, StackSamples);
            }
            // Magazines here are gun attachments with no rounds inside, so they're covered by the gun checks.
            if (item.HaveType("WEAPON") && GraphUtils.FindAllChildrenType<GameSlotInventory>(item).Count > 0)
            {
                AddSample(samples.Guns, id, SamplesPerKind);
            }
            else if (!item.HaveType("WEAPON") && GraphUtils.FindAllChildrenType<GameGridInventory>(item).Count > 0)
            {
                AddSample(samples.Bags, id, SamplesPerKind);
            }
        }

        private static void AddSample(List<string> list, string id, int max)
        {
            if (list.Count < max)
            {
                list.Add(id);
            }
        }

        private static int StackLimit(GameItem item)
        {
            try
            {
                return item.maxSlotAmountItemFunc == null ? 1 : item.maxSlotAmountItemFunc(item, null, null);
            }
            catch (Exception)
            {
                return 1;
            }
        }

        private static IEnumerator CheckPreparedItems(Samples samples)
        {
            Check("A finished wine keeps its state, name and sprite", () => WithPrepared(() => WineHelper.CreateFinishedWineForDebug(80), item =>
                Expect(item.GetTagReadonly("WINE_CONTAINER_STATE").GetInt() == 2, "the debug wine isn't finished")));
            // Counterfeit wine may be worth nothing, and then the trader rightly refuses it.
            Check("A finished counterfeit wine keeps its name", () => WithPrepared(() => WineHelper.CreateFinishedWineForDebug(80, counterfeit: true), item => null, refusedIsSkip: true));

            foreach (string id in samples.Liquids)
            {
                Check($"A part-filled liquid container ({id}) keeps its level", () => WithPrepared(() => DirectoryMaster.Item(id), item =>
                {
                    int current = item.GetTagReadonly("LIQUID_CONTAINER_CURRENT").GetInt();
                    int remove = current / 2;
                    if (remove <= 0 || !LiquidContainerHelper.CanRemoveVolume(item, remove))
                    {
                        return null;
                    }
                    LiquidContainerHelper.RemoveVolume(item, remove);
                    return Expect(item.GetTagReadonly("LIQUID_CONTAINER_CURRENT").GetInt() == current - remove, "couldn't change the level");
                }));
            }
            if (samples.Liquids.Count == 0)
            {
                Skip("A part-filled liquid container keeps its level", "no liquid container found");
            }

            foreach (string id in samples.Batteries)
            {
                Check($"A part-charged battery ({id}) keeps its charge", () => WithPrepared(() => DirectoryMaster.Item(id), item =>
                {
                    int charge = Math.Max(1, item.GetTagReadonly("power_source_item_max_energy").GetInt() / 3);
                    item.ModifyTag("power_source_item_energy", tag => tag.Enable().SetInt(charge));
                    return Expect(item.GetTagReadonly("power_source_item_energy").GetInt() == charge, "couldn't set the charge");
                }));
            }
            if (samples.Batteries.Count == 0)
            {
                Skip("A part-charged battery keeps its charge", "no battery found");
            }

            foreach (string id in samples.Stacks)
            {
                Check($"A stack of {StackCount} {id} keeps its count", () => WithPrepared(() => DirectoryMaster.Item(id), item =>
                {
                    item.unitCount = StackCount;
                    return null;
                }));
            }

            List<string> parts = samples.Mintable.Select(entry => entry.Id).ToList();
            foreach (string id in samples.Guns)
            {
                yield return Filled(id, parts, int.MaxValue, "A gun ({0}) with {1} attachments keeps them and its shape", null);
            }
            foreach (string id in samples.Bags)
            {
                yield return Filled(id, parts, MaxPartsPerBag, "A container ({0}) holding {1} items keeps them and their counts", samples);
            }
            if (samples.Guns.Count + samples.Bags.Count == 0)
            {
                Skip("Items with contents", "no gun or container found");
            }
        }

        // Puts catalogue items into the container's inventories until it holds `maxParts` of them, then round trips it.
        // With `samples`, the first one that survives is kept for the forged container listings.
        private static IEnumerator Filled(string id, List<string> parts, int maxParts, string name, Samples samples)
        {
            GameItem container;
            try
            {
                container = DirectoryMaster.Item(id);
            }
            catch (Exception e)
            {
                Fail(string.Format(name, id, "some"), $"couldn't create it: {e.Message}");
                yield break;
            }
            int placed = 0;
            bool keep = false;
            try
            {
                float frameStart = Time.realtimeSinceStartup;
                foreach (string partId in parts)
                {
                    List<GameInventory> open = GraphUtils.FindAllChildrenType<GameInventory>(container)
                        .Where(inventory => !(inventory is GameSlotInventory) || inventory.children.Count == 0)
                        .ToList();
                    if (placed >= maxParts || open.Count == 0)
                    {
                        break;
                    }
                    if (partId != id && TryPlace(partId, open))
                    {
                        placed++;
                    }
                    if (Time.realtimeSinceStartup - frameStart >= SecondsPerFrame)
                    {
                        yield return null;
                        frameStart = Time.realtimeSinceStartup;
                    }
                }
                string label = string.Format(name, id, placed);
                if (placed == 0)
                {
                    Skip(label, "nothing in the game fits inside it");
                }
                else
                {
                    int count = container.FindAllChildItems().Count;
                    bool ok = Check(label, () => WithRoundTrip(container, copy =>
                        Expect(copy.FindAllChildItems().Count == count, $"{copy.FindAllChildItems().Count} of {count} parts arrived")));
                    if (ok && samples != null && samples.Container == null)
                    {
                        samples.Container = container;
                        keep = true;
                    }
                }
            }
            finally
            {
                if (!keep)
                {
                    container.Destroy();
                }
            }
        }

        private static bool TryPlace(string partId, List<GameInventory> inventories)
        {
            GameItem part = null;
            try
            {
                part = DirectoryMaster.Item(partId);
                if (StackLimit(part) >= PartCount)
                {
                    part.unitCount = PartCount;
                }
                foreach (GameInventory inventory in inventories)
                {
                    if (GraphUtils.CanAccept(inventory, part))
                    {
                        GraphUtils.TryAcceptAll(inventory, part);
                        break;
                    }
                }
                return GraphUtils.DestroyIfUnplaced(part, "flea self-test");
            }
            catch (Exception)
            {
                part?.Destroy();
                return false;
            }
        }

        // Builds an item, lets `prepare` change it (null to go on, or why it couldn't), then round trips it.
        // With `refusedIsSkip`, an item the trader won't take is skipped rather than failed.
        private static string WithPrepared(Func<GameItem> create, Func<GameItem, string> prepare, bool refusedIsSkip = false)
        {
            GameItem item = create();
            try
            {
                string problem = prepare(item);
                if (problem != null)
                {
                    return "setup: " + problem;
                }
                if (!ItemCodec.CanEncode(item) || ItemCodec.IsWorthless(item))
                {
                    return (refusedIsSkip ? SkipMark : "") + "the trader wouldn't take it";
                }
                return RoundTrip(item);
            }
            finally
            {
                item.Destroy();
            }
        }

        // ---- Counter: anything else the player wants checked, as it is in their game.

        private static void CheckCounterItems()
        {
            List<GameItem> items;
            try
            {
                items = EmporiumEntry.Instance.backInvinvElement.childItems.Concat(EmporiumEntry.Instance.frontInvinvElement.childItems).ToList();
            }
            catch (Exception e)
            {
                Fail("Read the counter", e.Message);
                return;
            }
            if (items.Count == 0)
            {
                Skip("Counter items survive a round trip", "nothing on the counter. Optional: items placed there are checked as they are in your game.");
                return;
            }
            foreach (GameItem item in items)
            {
                string label = $"Counter item {item.GetDisplayName()} ({item.identifier}) survives a round trip";
                if (!ItemCodec.CanEncode(item) || ItemCodec.IsWorthless(item))
                {
                    Skip(label, "the trader wouldn't take it");
                    continue;
                }
                Check(label, () => RoundTrip(item));
            }
        }

        // ---- Forged listings, built by editing a real payload the way another player could.

        private static void CheckForgedListings(Samples samples)
        {
            GameItem single = samples.Single;
            if (single == null)
            {
                Skip("Forged listings", "no plain single item found in the catalogue");
                return;
            }
            string id = single.identifier;
            string payload = ItemCodec.Encode(single);
            SaveItemNode original = Nodes(payload)[0];

            Check("A listing for a different item than announced is refused", () =>
                Refused(ItemCodec.Decode(payload, id + "_other")));

            Check("A listing of an item this game doesn't have is refused", () =>
                Refused(DecodeEdited(payload, "flea_no_such_item", (e, nodes) => nodes[0].identifier = "flea_no_such_item")));

            if (samples.BlockedId != null)
            {
                Check($"A forged listing of a blocked item ({samples.BlockedId}) is refused", () =>
                    Refused(DecodeEdited(payload, samples.BlockedId, (e, nodes) => nodes[0].identifier = samples.BlockedId)));
            }
            else
            {
                Skip("A forged listing of a blocked item is refused", "no blocked item found");
            }

            Check("A listing claiming to be a character is refused", () =>
                Refused(DecodeEdited(payload, id, (e, nodes) => nodes[0].itemType = typeof(GameCharacterItem).FullName)));

            Check("A wrong itemType is replaced with this game's", () => WithDecoded(payload, id, (e, nodes) => nodes[0].itemType = "System.String", copy =>
                Expect(Nodes(ItemCodec.Encode(copy))[0].itemType == original.itemType, "itemType wasn't restored")));

            Check("Negative and huge item ids still decode", () => WithDecoded(payload, id, (e, nodes) => nodes[0].uuid = long.MaxValue - 3, copy =>
                Expect(copy.identifier == id, "wrong item")));

            Check("A forged 999 stack of a single item arrives as 1", () => WithDecoded(payload, id, (e, nodes) => nodes[0].unitCount = 999, copy =>
                Expect(copy.unitCount == 1, $"arrived as {copy.unitCount}")));

            Check("A forged value is capped", () => WithDecoded(payload, id, (e, nodes) => nodes[0].unitValue = long.MaxValue / 4, copy =>
            {
                long cap = Math.Max(original.unitValue * 10, 100);
                long value = Nodes(ItemCodec.Encode(copy))[0].unitValue;
                return Expect(value <= cap, $"unit value {value} is over the cap {cap}");
            }));

            Check("A name with markup is replaced with the local one", () => WithDecoded(payload, id, (e, nodes) => nodes[0].name = "<color=red>" + nodes[0].name + "</color>", copy =>
                Expect(copy.name == original.name, $"name is '{copy.name}'")));

            Check("A name in another language is replaced with the local one", () => WithDecoded(payload, id, (e, nodes) =>
            {
                e.lang = "flea_other_language";
                nodes[0].name = "Large " + nodes[0].name;
            }, copy => Expect(copy.name == original.name, $"name is '{copy.name}'")));

            Check("A sprite from another sheet shows the default sprite", () => WithDecoded(payload, id, (e, nodes) =>
            {
                nodes[0].spriteAtlasPath = "flea_bogus_sheet";
                nodes[0].spritePath = "flea_bogus_sprite";
            }, copy =>
            {
                SaveItemNode node = Nodes(ItemCodec.Encode(copy))[0];
                return Expect(node.spriteAtlasPath == original.spriteAtlasPath && node.spritePath == original.spritePath, $"sprite is {node.spriteAtlasPath}/{node.spritePath}");
            }));

            Check("A sprite this game doesn't have shows the default sprite", () => WithDecoded(payload, id, (e, nodes) => nodes[0].spritePath = "flea_bogus_sprite", copy =>
                Expect(Nodes(ItemCodec.Encode(copy))[0].spritePath == original.spritePath, "sprite kept")));

            Check("A seller name loses its markup", () => WithDecoded(payload, id, (e, nodes) => e.seller = "<b>Evil</b>", copy =>
            {
                string seller = SellerOf(copy);
                return Expect(!string.IsNullOrEmpty(seller) && !seller.Contains("<"), $"seller is '{seller}'");
            }));

            Check("A listing whose item contains itself is refused", () =>
                Refused(DecodeEdited(payload, id, (e, nodes) =>
                {
                    nodes[0].childItems.Add(nodes[0].uuid);
                    nodes[0].childItemInventoryNode.Add(0);
                })));

            Check("A listing pointing at a missing child is refused", () =>
                Refused(DecodeEdited(payload, id, (e, nodes) =>
                {
                    nodes[0].childItems.Add(nodes[0].uuid + 12345);
                    nodes[0].childItemInventoryNode.Add(0);
                })));

            Check("A listing with no item state is refused", () =>
                Refused(ItemCodec.Decode(Envelopes.Write(new ItemEnvelope { lang = "en", seller = "x", state = "" }), id)));

            Check("A listing that isn't JSON is skipped", () =>
            {
                try
                {
                    return Refused(ItemCodec.Decode("{not json", id));
                }
                catch (Exception)
                {
                    // TraderVisit.PlaceOffers catches this and logs "Skipped offer".
                    return null;
                }
            });

            if (samples.Container == null)
            {
                Skip("A container listing with forged ids keeps its contents", "no container with contents found in the catalogue; use the counter checks");
                return;
            }
            GameItem container = samples.Container;
            string containerPayload = ItemCodec.Encode(container);
            int childCount = container.FindAllChildItems().Count;
            Check($"A container ({container.identifier}) listing with negative ids keeps its contents", () =>
                WithDecoded(containerPayload, container.identifier, (e, nodes) =>
                {
                    Dictionary<long, long> map = nodes.Select((node, index) => (node.uuid, index)).ToDictionary(pair => pair.uuid, pair => -1000L * (pair.index + 1));
                    foreach (SaveItemNode node in nodes)
                    {
                        node.uuid = map[node.uuid];
                        node.childItems = node.childItems.Select(child => map[child]).ToList();
                    }
                }, copy => Expect(copy.FindAllChildItems().Count == childCount, $"{copy.FindAllChildItems().Count} of {childCount} parts arrived")));

            Check($"A container ({container.identifier}) listing that shares a child is refused", () =>
                Refused(DecodeEdited(containerPayload, container.identifier, (e, nodes) =>
                {
                    nodes[0].childItems.Add(nodes[0].childItems[0]);
                    nodes[0].childItemInventoryNode.Add(nodes[0].childItemInventoryNode[0]);
                })));
        }

        // ---- Fake items: authenticity travels as a mark the buyer's game rebuilds.

        private static readonly (string Item, string Category, string Shown, string Real)[] ValidMarks =
        {
            (CigaretteId, "CATEGORY_GENUINE_CIGARETTE", "genuineCigarette", "genuineCigarette"),
            (CigaretteId, "CATEGORY_GENUINE_CIGARETTE", "genuineCigarette", "counterfeitCigarette"),
            (CigaretteId, "CATEGORY_GENUINE_CIGARETTE", "counterfeitCigarette", "counterfeitCigarette"),
            (CigaretteId, "CATEGORY_GENUINE_CIGARETTE", "counterfeitCigarette", "genuineCigarette"),
            (StampId, "CATEGORY_GENUINE_STAMP", "genuineStamp", "genuineStamp"),
            (StampId, "CATEGORY_GENUINE_STAMP", "genuineStamp", "counterfeitStamp"),
            (StampId, "CATEGORY_GENUINE_STAMP", "counterfeitStamp", "counterfeitStamp"),
            (InjectorId, "CATEGORY_GENUINE_INJECTOR", "genuineInjector", "genuineInjector"),
            (InjectorId, "CATEGORY_GENUINE_INJECTOR", "genuineInjector", "expiredInjector"),
            (InjectorId, "CATEGORY_GENUINE_INJECTOR", "genuineInjector", "counterfeitInjector"),
            (InjectorId, "CATEGORY_GENUINE_INJECTOR", "counterfeitInjector", "counterfeitInjector")
        };

        private static void CheckFakeMarks()
        {
            foreach (var (itemId, category, shown, real) in ValidMarks)
            {
                if (!DirectoryMaster.Has<GameItem>(itemId))
                {
                    Skip($"Mark {shown}/{real} on {itemId}", "item not in this game");
                    continue;
                }
                ItemMark mark = new ItemMark { node = 0, category = category, shown = shown, real = real };
                Check($"A {itemId} showing {shown} that is really {real} arrives the same", () => WithFresh(itemId, item =>
                {
                    FakeCheck.Apply(mark, item);
                    string applied = MarkOf(item, category);
                    if (applied != $"{shown}/{real}")
                    {
                        return $"applying gave {applied ?? "no mark"}";
                    }
                    return WithRoundTrip(item, copy => Expect(MarkOf(copy, category) == applied,
                        $"arrived as {MarkOf(copy, category) ?? "no mark"} ({MarkDiagnostics(item, copy)})"));
                }));
            }

            if (DirectoryMaster.Has<GameItem>(CigaretteId))
            {
                Check("A mark with a made-up condition is ignored", () => WithFresh(CigaretteId, item =>
                {
                    string before = MarkOf(item, "CATEGORY_GENUINE_CIGARETTE");
                    FakeCheck.Apply(new ItemMark { category = "CATEGORY_GENUINE_CIGARETTE", shown = "genuineCigarette", real = "flea_bogus" }, item);
                    return Expect(MarkOf(item, "CATEGORY_GENUINE_CIGARETTE") == before, "the item changed");
                }));
            }
            if (DirectoryMaster.Has<GameItem>(StampId))
            {
                Check("A cigarette mark on a stamp is ignored", () => WithFresh(StampId, item =>
                {
                    int before = item.itemFeatures.Count;
                    FakeCheck.Apply(new ItemMark { category = "CATEGORY_GENUINE_CIGARETTE", shown = "genuineCigarette", real = "genuineCigarette" }, item);
                    return Expect(item.itemFeatures.Count == before && MarkOf(item, "CATEGORY_GENUINE_CIGARETTE") == null, "the stamp got a cigarette mark");
                }));
            }
        }

        // ---- Minted offers: picked from the game's own items, near the sold item's price.

        private static void CheckMintedOffers(Samples samples)
        {
            if (samples.Mintable.Count == 0)
            {
                Skip("Minted offers", "no mintable items found");
                return;
            }
            System.Random random = new System.Random(1);
            for (int index = 0; index < Math.Min(MintSamples, samples.Mintable.Count); index++)
            {
                var sold = samples.Mintable[random.Next(samples.Mintable.Count)];
                Check($"A minted offer for a sold {sold.Id} ({sold.Value}) is a sellable item at about that price", () =>
                {
                    GameItem item = MintCatalogue.Create(new Offer { value = sold.Value, types = sold.Types }, null);
                    if (item == null)
                    {
                        return $"nothing was minted (types: {string.Join(",", sold.Types)})";
                    }
                    try
                    {
                        long value = item.GetBaseValue();
                        if (Math.Abs(value - sold.Value) > sold.Value * Assortment.ClosePriceRange)
                        {
                            return $"{item.identifier} costs {value}";
                        }
                        if (ItemCodec.IsBlocked(item) || !ItemCodec.CanEncode(item))
                        {
                            return $"{item.identifier} can't be resold";
                        }
                        return Expect(SellerOf(item) == ModEntry.L("seller_unknown"), $"seller is '{SellerOf(item)}'");
                    }
                    finally
                    {
                        item.Destroy();
                    }
                });
            }
            foreach (string category in TypeHelper.MAJOR_TYPES_LIST)
            {
                if (!MintCatalogue.HasAny(category))
                {
                    continue;
                }
                Check($"A local offer for {category} comes from that category", () =>
                {
                    GameItem item = MintCatalogue.CreateAny(category);
                    if (item == null)
                    {
                        return "nothing was minted";
                    }
                    try
                    {
                        return Expect(item.GetGameItemType().Contains(category), $"{item.identifier} isn't {category}");
                    }
                    finally
                    {
                        item.Destroy();
                    }
                });
            }
        }

        // ---- Helpers.

        // Sells and buys back a copy of the item, and says what changed, or null.
        private static string RoundTrip(GameItem item)
        {
            return WithRoundTrip(item, copy => null);
        }

        private static string WithRoundTrip(GameItem item, Func<GameItem, string> check)
        {
            string payload = ItemCodec.Encode(item);
            GameItem copy = ItemCodec.Decode(payload, item.identifier);
            if (copy == null)
            {
                return "it decoded to nothing, so the buyer would get a stand-in";
            }
            // The game's own load changes some items: modded items gain types, and parts in a container
            // get their shape offsets reset. So the copy is compared with a plain game load of the same payload.
            GameItem saved = ItemCodec.DecodeAsSaved(payload);
            try
            {
                List<SaveItemNode> sent = Nodes(payload);
                List<SaveItemNode> before = saved != null ? Nodes(ItemCodec.Encode(saved)) : sent;
                List<SaveItemNode> after = Nodes(ItemCodec.Encode(copy));
                if (sent.Count != after.Count)
                {
                    return $"{sent.Count} parts went in, {after.Count} came out";
                }
                for (int index = 0; index < before.Count && index < after.Count; index++)
                {
                    string difference = Difference(before[index], after[index]);
                    if (difference != null)
                    {
                        return $"part {index} ({before[index].identifier}) changed {difference}";
                    }
                }
                string seller = SellerOf(copy);
                if (string.IsNullOrEmpty(seller))
                {
                    return "no seller line";
                }
                return check(copy);
            }
            finally
            {
                copy.Destroy();
                saved?.Destroy();
            }
        }

        private static string Difference(SaveItemNode a, SaveItemNode b)
        {
            (string Field, string A, string B)[] fields =
            {
                ("identifier", a.identifier, b.identifier),
                ("count", a.unitCount.ToString(), b.unitCount.ToString()),
                ("value", a.unitValue.ToString(), b.unitValue.ToString()),
                ("late value", a.lateUnitValue.ToString(), b.lateUnitValue.ToString()),
                ("base value", a.unitBaseValue.ToString(), b.unitBaseValue.ToString()),
                ("name", a.name, b.name),
                ("description", a.shortDescription, b.shortDescription),
                ("sprite", a.spriteAtlasPath + "/" + a.spritePath, b.spriteAtlasPath + "/" + b.spritePath),
                ("itemType", a.itemType, b.itemType),
                ("types", string.Join(",", a.itemTypes ?? new List<string>()), string.Join(",", b.itemTypes ?? new List<string>())),
                ("parts inside", a.childItems.Count.ToString(), b.childItems.Count.ToString()),
                ("shape", JsonUtility.ToJson(a.itemShape), JsonUtility.ToJson(b.itemShape)),
                ("modified shape", JsonUtility.ToJson(a.itemModifiedShape), JsonUtility.ToJson(b.itemModifiedShape)),
                ("state", JsonUtility.ToJson(a.itemState), JsonUtility.ToJson(b.itemState))
            };
            foreach (var (field, before, after) in fields)
            {
                if (before != after)
                {
                    return $"{field}: {Shorten(before)} -> {Shorten(after)}";
                }
            }
            return null;
        }

        private static string Shorten(string text)
        {
            return text == null ? "null" : text.Length > 200 ? text.Substring(0, 200) + "..." : text;
        }

        private static List<SaveItemNode> Nodes(string payload)
        {
            return JsonUtility.FromJson<SaveState>(Envelopes.Read(payload).state).saveItems;
        }

        private static GameItem DecodeEdited(string payload, string expectedId, Action<ItemEnvelope, List<SaveItemNode>> edit)
        {
            ItemEnvelope envelope = Envelopes.Read(payload);
            SaveState state = JsonUtility.FromJson<SaveState>(envelope.state);
            edit(envelope, state.saveItems);
            envelope.state = JsonUtility.ToJson(state);
            return ItemCodec.Decode(Envelopes.Write(envelope), expectedId);
        }

        // Passes when the listing decoded to nothing. Anything that did decode is destroyed.
        private static string Refused(GameItem item)
        {
            if (item == null)
            {
                return null;
            }
            item.Destroy();
            return $"it decoded as {item.identifier}";
        }

        private static string WithDecoded(string payload, string expectedId, Action<ItemEnvelope, List<SaveItemNode>> edit, Func<GameItem, string> check)
        {
            GameItem item = DecodeEdited(payload, expectedId, edit);
            if (item == null)
            {
                return "it decoded to nothing";
            }
            try
            {
                return check(item);
            }
            finally
            {
                item.Destroy();
            }
        }

        private static string WithFresh(string id, Func<GameItem, string> check)
        {
            GameItem item = DirectoryMaster.Item(id, isOwned: false);
            try
            {
                return check(item);
            }
            finally
            {
                item.Destroy();
            }
        }

        private static string MarkOf(GameItem item, string category)
        {
            ItemFeature feature = item.itemFeatures.FirstOrDefault(f => f.useCondition && f.GetCategory() == category);
            if (feature == null)
            {
                return null;
            }
            string real = feature.realCondition?.identifier;
            string shown = feature.IsExposed() ? real : feature.fakeCondition?.identifier;
            return $"{shown}/{real}";
        }

        // Where a mark went missing: what the payload carried, and what each side ended up with.
        private static string MarkDiagnostics(GameItem item, GameItem copy)
        {
            List<ItemMark> marks = Envelopes.Read(ItemCodec.Encode(item)).marks;
            string sent = marks == null ? "no marks list" : string.Join(",", marks.Select(m => $"node {m.node} {m.category} {m.shown}/{m.real}"));
            return $"payload: [{sent}]; seller id {item.uniqueId}, buyer id {copy.uniqueId}; buyer features: [{FeaturesOf(copy)}]";
        }

        private static string FeaturesOf(GameItem item)
        {
            return string.Join(",", item.itemFeatures.Select(f => $"{f.identifier}:{f.GetCategory()}:{(f.useCondition ? "cond" : "plain")}"));
        }

        private static string SellerOf(GameItem item)
        {
            return item.state.dict.TryGetValue(ItemCodec.SellerTag, out TagState tag) ? tag.GetString() : null;
        }

        private static string Expect(bool condition, string failure) => condition ? null : failure;

        // A body's answer starting with this is a reason to skip, not a failure.
        private const string SkipMark = "\u0001skip:";

        // The body returns null to pass, or what went wrong. Returns whether it passed.
        private static bool Check(string name, Func<string> body)
        {
            string problem;
            try
            {
                problem = body();
            }
            catch (Exception e)
            {
                problem = $"threw {e.GetType().Name}: {e.Message}";
            }
            if (problem != null && problem.StartsWith(SkipMark))
            {
                Skip(name, problem.Substring(SkipMark.Length));
                return false;
            }
            Report(name, problem == null, problem);
            return problem == null;
        }

        private static void Report(string name, bool ok, string problem)
        {
            if (ok)
            {
                passed++;
                ModEntry.Log.Log($"{Prefix}pass  {name}");
            }
            else
            {
                Fail(name, problem);
            }
        }

        private static void Fail(string name, string problem)
        {
            failed++;
            ModEntry.Log.Warning($"{Prefix}FAIL  {name}: {problem}");
        }

        private static void Skip(string name, string reason)
        {
            skipped++;
            ModEntry.Log.Log($"{Prefix}skip  {name}: {reason}");
        }
    }
}

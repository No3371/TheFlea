using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using UnityEngine;

namespace TheFlea
{
    // Moves one item between games in the game's own save format. Incoming payloads
    // come from other players, so they are checked and partly overwritten before decoding.
    internal static class ItemCodec
    {
        private const string BagId = "save_bag";
        private const int MaxNodes = 32;
        // Far larger than any container's grid, only so a forged position can't be absurd.
        private const int MaxGridCoordinate = 64;
        private const int MaxCount = 999;
        private const string WineBottleId = "wine_bottle";
        private const int FinishedWineState = 2;
        private const long ValueCapFactor = 10;
        private const long ValueCapMinimum = 100;
        private const long ValueFloorDivisor = 10;

        // Items that aren't for sale, checked on the local definition because a payload's own types can be forged.
        private static readonly string[] BlockedTypes = { "MACHINE", TypeHelper.NOT_FOR_RESALE, "ACCESS_CARD" };

        internal const string SellerTag = "FLEA_SELLER_STRING";

        // Each tag a seller could show, and the true value it should match.
        private static readonly (string Shown, string Real)[] ShownTags =
        {
            ("DURABILITY_FAKE_TAG", "DURABILITY_TAG"),
            ("MAX_DURABILITY_FAKE_TAG", "MAX_DURABILITY_TAG"),
            ("INGOT_PURITY_FAKE_TAG", "INGOT_PURITY_TAG")
        };

        private static readonly string[] SellerOnlyTags =
        {
            "STOLEN_TAG", "STOLEN_VALUE_INT", "BOUGHT_PRICE_TAG", "ALREADY_CONFRONTED_TAG",
            "HOPPER_LINK_INT", "PARENT_ITEM_ID", "ASSOCIATED_HYDROPONIC_ID", SellerTag
        };

        public static bool CanEncode(GameItem item)
        {
            if (item == null || IsBlocked(item) || IsHot(item))
            {
                return false;
            }
            List<GameItem> children = item.FindAllChildItems();
            return children.Count < MaxNodes && !children.Any(child => IsBlocked(child) || IsHot(child));
        }

        public static bool HasHotPart(GameItem item)
        {
            return IsHot(item) || item.FindAllChildItems().Any(IsHot);
        }

        public static bool HasContrabandPart(GameItem item)
        {
            return ContrabandHelper.IsContraband(item) || item.FindAllChildItems().Any(ContrabandHelper.IsContraband);
        }

        // Stolen goods stop being hot as their heat cools each night. This isn't checked on arrival: the
        // seller's game strips the stolen tags, and an item that starts out stolen arrives cooled.
        private static bool IsHot(GameItem item)
        {
            return StolenHelper.IsStolenItem(item) && StolenHelper.GetFoundChance(item) > 0;
        }

        public static bool IsWorthless(GameItem item)
        {
            return item.GetBaseValue() <= 0;
        }

        internal static bool IsBlocked(GameItem item)
        {
            return item is GameCharacterItem
                || ItemSpawnerHiddenList.IsHidden(item.identifier)
                || ContrabandHelper.IsContraband(item)
                || BlockedTypes.Any(type => item.HaveType(type) || item.IsTag(type));
        }

        public static string Encode(GameItem item)
        {
            PlayerStore store = PlayerStore.Instance;
            if (store.savedItemFeatureList == null)
            {
                store.savedItemFeatureList = new List<ItemFeature>();
            }
            SaveState state = JsonUtility.FromJson<SaveState>(store.EncodeItem(item));
            foreach (SaveItemNode node in state.saveItems)
            {
                StripSellerTags(node);
            }
            return Envelopes.Write(new ItemEnvelope
            {
                lang = ModEntry.L("lang_code"),
                seller = CleanSellerName(OwnSellerName()),
                state = JsonUtility.ToJson(state),
                marks = FakeCheck.Describe(item, state.saveItems)
            });
        }

        // Returns null when any item in the payload is missing here, isn't for sale, or the payload is
        // unusable or worth nothing. The caller replaces it with a local stand-in at a fair price.
        // Payload text that isn't JSON throws, and the caller skips that offer.
        public static GameItem Decode(string payload, string expectedId)
        {
            ItemEnvelope envelope = Envelopes.Read(payload);
            if (string.IsNullOrEmpty(envelope?.state))
            {
                return null;
            }
            List<SaveItemNode> nodes = JsonUtility.FromJson<SaveState>(envelope.state)?.saveItems;
            if (nodes == null || nodes.Count == 0 || nodes.Count > MaxNodes || nodes[0].identifier != expectedId || !IsTree(nodes))
            {
                return null;
            }
            Renumber(nodes);
            foreach (SaveItemNode node in nodes)
            {
                if (!DirectoryMaster.Has<GameItem>(node.identifier) || node.itemType == typeof(GameCharacterItem).FullName || IsBlockedId(node.identifier))
                {
                    return null;
                }
            }
            bool sameLanguage = envelope.lang == ModEntry.L("lang_code");
            foreach (SaveItemNode node in nodes)
            {
                Sanitize(node, sameLanguage, isRoot: node == nodes[0]);
            }
            Dictionary<int, ItemMark> marks = (envelope.marks ?? new List<ItemMark>())
                .Where(mark => mark != null && mark.node >= 0 && mark.node < nodes.Count)
                .GroupBy(mark => nodes[mark.node].uniqueId)
                .ToDictionary(group => group.Key, group => group.First());
            GameItem item = DecodeThroughBag(nodes);
            if (item != null)
            {
                foreach (GameItem part in item.FindAllChildItems().Append(item).Distinct())
                {
                    RestoreWineName(part);
                    if (marks.TryGetValue(part.uniqueId, out ItemMark mark))
                    {
                        FakeCheck.Apply(mark, part);
                    }
                }
                if (IsWorthless(item))
                {
                    item.Destroy();
                    return null;
                }
            }
            string seller = CleanSellerName(envelope.seller);
            item?.ModifyTag(SellerTag, tag => tag.SetString(seller.Length > 0 ? seller : ModEntry.L("seller_anonymous")), skipValidate: true);
            return item;
        }

        // Loads the payload the way the game loads its own saves, with none of the checks or repairs above.
        // The self-test compares against it, so changes the game itself makes on loading aren't blamed on the mod.
        internal static GameItem DecodeAsSaved(string payload)
        {
            List<SaveItemNode> nodes = JsonUtility.FromJson<SaveState>(Envelopes.Read(payload).state).saveItems;
            if (nodes == null || nodes.Count == 0 || !IsTree(nodes))
            {
                return null;
            }
            Renumber(nodes);
            return DecodeThroughBag(nodes);
        }

        // The game loads child links recursively, so a payload with a loop or a shared child could hang or
        // crash it. Accepts only a tree hanging from the first node, with each link naming its inventory.
        private static bool IsTree(List<SaveItemNode> nodes)
        {
            Dictionary<long, SaveItemNode> byId = new Dictionary<long, SaveItemNode>();
            foreach (SaveItemNode node in nodes)
            {
                if (node == null || node.childItems == null || node.childItemInventoryNode == null
                    || node.childItems.Count != node.childItemInventoryNode.Count || byId.ContainsKey(node.uuid))
                {
                    return false;
                }
                byId[node.uuid] = node;
            }
            HashSet<long> reached = new HashSet<long> { nodes[0].uuid };
            Queue<SaveItemNode> queue = new Queue<SaveItemNode>();
            queue.Enqueue(nodes[0]);
            while (queue.Count > 0)
            {
                foreach (long child in queue.Dequeue().childItems)
                {
                    if (!byId.TryGetValue(child, out SaveItemNode next) || !reached.Add(child))
                    {
                        return false;
                    }
                    queue.Enqueue(next);
                }
            }
            return reached.Count == nodes.Count;
        }

        // The payload's own ids could be negative or huge, and DecodeThroughBag adds an offset to them, so they
        // could collide with the bag's ids or wrap. Numbers the nodes 0..n-1 instead. Needs a tree that passed IsTree.
        private static void Renumber(List<SaveItemNode> nodes)
        {
            Dictionary<long, long> ids = new Dictionary<long, long>();
            for (int index = 0; index < nodes.Count; index++)
            {
                ids[nodes[index].uuid] = index;
            }
            foreach (SaveItemNode node in nodes)
            {
                node.uuid = ids[node.uuid];
                node.childItems = node.childItems.Select(child => ids[child]).ToList();
            }
        }

        public static void SetUnknownSeller(GameItem item)
        {
            item.ModifyTag(SellerTag, tag => tag.SetString(ModEntry.L("seller_unknown")), skipValidate: true);
        }

        public static void AddSellerLine(RichTextBuilder builder, GameItem item)
        {
            if (item.state.dict.TryGetValue(SellerTag, out TagState tag) && !string.IsNullOrEmpty(tag.GetString()))
            {
                builder.AddLine(ModEntry.L("tooltip_seller", ModEntry.HideSellerNames.Value ? ModEntry.L("seller_hidden") : tag.GetString()));
            }
        }

        private static string OwnSellerName()
        {
            return !ModEntry.SellAnonymously.Value && SteamManager.Initialized ? SteamFriends.GetPersonaName() : "";
        }

        private static string CleanSellerName(string name) => Identity.CleanSellerName(name);

        private static void StripSellerTags(SaveItemNode node)
        {
            foreach (string tag in SellerOnlyTags)
            {
                node.itemState?.dict.Remove(tag);
            }
        }

        private static bool IsBlockedId(string identifier)
        {
            GameItem fresh = DirectoryMaster.Item(identifier);
            try
            {
                return IsBlocked(fresh);
            }
            finally
            {
                fresh.Destroy();
            }
        }

        // An item with no stack rule of its own holds one, unless it sits in something that allows more, like a magazine.
        private static int MaxStack(GameItem fresh, bool isRoot)
        {
            if (fresh.maxSlotAmountItemFunc == null)
            {
                return isRoot ? 1 : MaxCount;
            }
            try
            {
                return Mathf.Clamp(fresh.maxSlotAmountItemFunc(fresh, null, null), 1, MaxCount);
            }
            catch (Exception)
            {
                return MaxCount;
            }
        }

        private static void Sanitize(SaveItemNode node, bool sameLanguage, bool isRoot)
        {
            GameItem fresh = DirectoryMaster.Item(node.identifier);
            try
            {
                StripSellerTags(node);
                node.itemType = LocalItemType(fresh);
                node.uniqueId = PlayerStore.Instance.GetUniqueID();
                node.unitCount = Mathf.Clamp(node.unitCount, 1, MaxStack(fresh, isRoot));
                long cap = Math.Max(fresh.unitValue * ValueCapFactor, ValueCapMinimum);
                node.unitValue = Math.Min(Math.Max(node.unitValue, fresh.unitValue / ValueFloorDivisor), cap);
                node.lateUnitValue = Math.Min(Math.Max(node.lateUnitValue, 0), cap);
                node.backupUnitValue = fresh.backupUnitValue;
                node.unitBaseValue = fresh.unitBaseValue;
                node.itemShape = new GridShapeBuilder(fresh.shape);
                // The cells come from this game, so a listing can't shrink an item. Where a part sits in its
                // container, and which way it's turned, come from the listing, or rotated parts would overlap.
                GridShapeBuilder sent = node.itemModifiedShape;
                node.itemModifiedShape = new GridShapeBuilder(fresh.modifiedShape);
                if (sent != null)
                {
                    node.itemModifiedShape.SetTransform(Mathf.Clamp(sent.minX, 0, MaxGridCoordinate), Mathf.Clamp(sent.minY, 0, MaxGridCoordinate),
                        sent.flipped, ((sent.orientation % 4) + 4) % 4);
                }
                TrustRealTags(node);
                if (!sameLanguage || !IsPlausibleName(node.name, fresh.name))
                {
                    node.name = fresh.name;
                }
                if (!sameLanguage || !IsPlausibleName(node.shortDescription, fresh.shortDescription))
                {
                    node.shortDescription = fresh.shortDescription;
                }
                if (!sameLanguage || !IsPlausibleName(node.flavorText, fresh.flavorText))
                {
                    node.flavorText = fresh.flavorText;
                }
                if (!CanUseSprite(node, fresh))
                {
                    node.spriteAtlasPath = fresh.spriteAtlasPath;
                    node.spritePath = fresh.spritePath;
                }
            }
            finally
            {
                fresh.Destroy();
            }
        }

        // The payload's item type is the seller's word, so it's replaced with what this game writes for the item.
        private static string LocalItemType(GameItem fresh)
        {
            PlayerStore store = PlayerStore.Instance;
            if (store.savedItemFeatureList == null)
            {
                store.savedItemFeatureList = new List<ItemFeature>();
            }
            return JsonUtility.FromJson<SaveState>(store.EncodeItem(fresh)).saveItems[0].itemType;
        }

        private static bool IsPlausibleName(string name, string freshName) => Identity.IsPlausibleName(name, freshName);

        // Blocks sprites from other sheets, ones this game doesn't have, and ones that wouldn't fit
        // the item's default shape. The sheet is checked first because it decides which game files get loaded.
        private static bool CanUseSprite(SaveItemNode node, GameItem fresh)
        {
            if (node.spriteAtlasPath != fresh.spriteAtlasPath)
            {
                return false;
            }
            Sprite sent = FindSprite(fresh, node.spritePath);
            Sprite standard = FindSprite(fresh, fresh.spritePath);
            return sent != null && standard != null && sent.rect.size == standard.rect.size;
        }

        private static Sprite FindSprite(GameItem fresh, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            if (!fresh.useSpriteFromMod)
            {
                return RenderHandler.TryLoadFromAtlas(fresh.spriteAtlasPath, name);
            }
            LoadedMod mod = ModLoader.Mods.Find(m => m.Manifest.ID == fresh.spriteAtlasPath);
            return mod != null && mod.Sprites.TryGetValue(name, out Sprite sprite) ? sprite : null;
        }

        // Finished wine keeps the bottle's item ID, so its name comes from its tags instead.
        private static void RestoreWineName(GameItem item)
        {
            if (item.identifier != WineBottleId || item.GetTagReadonly("WINE_CONTAINER_STATE").GetInt() != FinishedWineState)
            {
                return;
            }
            string wine = item.IsTag("WINE_COUNTERFEIT_TAG") ? "gloomberry" : "bloomberry";
            item.name = LocHelper.GetLocalizedItem($"item_wine_{wine}_name");
            item.shortDescription = LocHelper.GetLocalizedItem($"item_wine_{wine}_desc");
        }

        private static void TrustRealTags(SaveItemNode node)
        {
            Dictionary<string, TagState> tags = node.itemState?.dict;
            if (tags == null)
            {
                return;
            }
            foreach ((string shown, string real) in ShownTags)
            {
                if (tags.TryGetValue(shown, out TagState shownTag) && tags.TryGetValue(real, out TagState realTag))
                {
                    shownTag.SetInt(realTag.GetInt());
                    shownTag.SetString(realTag.GetString());
                }
            }
        }

        // PlayerStore.DecodeSaveItem is the only path that runs the game's after-load fixups
        // (machine upgrades, water sprites, onLoaded callbacks...), and it expects items inside a save bag.
        private static GameItem DecodeThroughBag(List<SaveItemNode> nodes)
        {
            PlayerStore store = PlayerStore.Instance;
            GameItem bag = DirectoryMaster.Item(BagId);
            GameItem receiver = DirectoryMaster.Item(BagId);
            try
            {
                SaveState wrapped = JsonUtility.FromJson<SaveState>(store.EncodeItem(bag));
                int inventoryNode = InventoryNodeIndex(bag);
                long offset = wrapped.saveItems.Max(n => n.uuid) + 1;
                foreach (SaveItemNode node in nodes)
                {
                    node.uuid += offset;
                    node.childItems = node.childItems.Select(child => child + offset).ToList();
                }
                wrapped.saveItems[0].childItems.Add(nodes[0].uuid);
                wrapped.saveItems[0].childItemInventoryNode.Add(inventoryNode);
                wrapped.saveItems.AddRange(nodes);

                GameInventory target = (GameInventory)receiver.contentWindow.child;
                store.DecodeSaveItem(JsonUtility.ToJson(wrapped), target);
                GameItem item = target.children.OfType<GameItem>().FirstOrDefault();
                if (item != null)
                {
                    target.Expel(item);
                }
                return item;
            }
            finally
            {
                bag.Destroy();
                receiver.Destroy();
            }
        }

        // Mirrors how SaveManager numbers a bag's non-item nodes, so the saved link finds its inventory.
        private static int InventoryNodeIndex(GameItem bag)
        {
            GraphNodeStorage inventory = bag.contentWindow.child;
            Queue<GraphNodeStorage> queue = new Queue<GraphNodeStorage>();
            Dictionary<GraphNodeStorage, int> numbers = new Dictionary<GraphNodeStorage, int>();
            queue.Enqueue(bag);
            while (queue.Count > 0)
            {
                GraphNodeStorage node = queue.Dequeue();
                if (node != bag)
                {
                    if (node is GameItem)
                    {
                        continue;
                    }
                    if (!numbers.ContainsKey(node))
                    {
                        numbers.Add(node, numbers.Count);
                    }
                    if (node == inventory)
                    {
                        return numbers[node];
                    }
                }
                foreach (GraphNodeStorage child in node.children)
                {
                    queue.Enqueue(child);
                }
            }
            throw new InvalidOperationException(BagId + " has no inventory");
        }
    }
}

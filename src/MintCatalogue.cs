using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TheFlea
{
    // Picks the game's own items for minted offers. Reading an item's value and types means
    // creating it, so the list is built once when a game loads, a few milliseconds per frame.
    internal static class MintCatalogue
    {
        private const float SecondsPerFrame = 0.004f;

        private class Entry
        {
            public string id;
            public long value;
            public List<string> types;
        }

        private static List<Entry> entries;
        private static bool loading;

        public static void LoadInBackground()
        {
            ModEntry.Host.StartCoroutine(Load());
        }

        // A visit can ask for the list while it's still being built, so it waits for that build to finish.
        public static IEnumerator Load()
        {
            while (loading)
            {
                yield return null;
            }
            if (entries != null)
            {
                yield break;
            }
            loading = true;
            try
            {
                List<Entry> found = new List<Entry>();
                float frameStart = Time.realtimeSinceStartup;
                foreach (string id in ItemIdentifiers())
                {
                    if (ItemSpawnerHiddenList.IsHidden(id))
                    {
                        continue;
                    }
                    AddIfMintable(id, found);
                    if (Time.realtimeSinceStartup - frameStart >= SecondsPerFrame)
                    {
                        yield return null;
                        frameStart = Time.realtimeSinceStartup;
                    }
                }
                entries = found;
            }
            finally
            {
                loading = false;
            }
        }

        private static List<string> ItemIdentifiers()
        {
            try
            {
                return DirectoryMaster.GetIdentifierList<GameItem>();
            }
            catch (Exception e)
            {
                ModEntry.Log.Warning("Could not list items for minted offers: " + e.Message);
                return new List<string>();
            }
        }

        private static void AddIfMintable(string id, List<Entry> found)
        {
            try
            {
                GameItem item = DirectoryMaster.Item(id, isOwned: false);
                try
                {
                    long value = item.GetBaseValue();
                    if (value > 0 && ItemCodec.CanEncode(item))
                    {
                        found.Add(new Entry { id = id, value = value, types = item.GetGameItemType().ToList() });
                    }
                }
                finally
                {
                    item.Destroy();
                }
            }
            catch (Exception e)
            {
                ModEntry.Log.Warning($"Skipped {id} for minted offers: {e.Message}");
            }
        }

        // Returns null when nothing here is close enough in price, and the trader doesn't bring the offer.
        public static GameItem Create(Offer offer, string category)
        {
            Entry entry = Pick(WantedTypes(offer, category), offer.value);
            if (entry == null)
            {
                return null;
            }
            GameItem item = DirectoryMaster.Item(entry.id, isOwned: false);
            ItemCodec.SetUnknownSeller(item);
            return item;
        }

        public static bool HasAny(string category)
        {
            return entries != null && entries.Any(entry => InCategory(entry, category));
        }

        // A random item from the category, or from anything without one, for offers with no listing to copy.
        public static GameItem CreateAny(string category)
        {
            List<Entry> matching = entries?.Where(entry => InCategory(entry, category)).ToList();
            if (matching == null || matching.Count == 0)
            {
                return null;
            }
            GameItem item = DirectoryMaster.Item(matching[UnityEngine.Random.Range(0, matching.Count)].id, isOwned: false);
            ItemCodec.SetUnknownSeller(item);
            return item;
        }

        private static bool InCategory(Entry entry, string category)
        {
            return string.IsNullOrEmpty(category) || entry.types.Contains(category);
        }

        // A sold item can carry minor types too, and those shouldn't decide what comes back.
        private static List<string> WantedTypes(Offer offer, string category)
        {
            return Assortment.WantedTypes(offer.types, category, TypeHelper.MAJOR_TYPES_LIST);
        }

        private static Entry Pick(List<string> types, double value)
        {
            if (entries == null)
            {
                return null;
            }
            return Assortment.Pick(Assortment.Matching(entries, entry => entry.types, types), entry => entry.value, value, count => UnityEngine.Random.Range(0, count));
        }
    }
}

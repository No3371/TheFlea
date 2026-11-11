using System;
using System.Collections.Generic;
using UnityEngine;

namespace TheFlea
{
    // The trader's business card, built like the game's own contact cards. It's left on the counter with the
    // morning paper the first time a run opens its shutter with the mod installed, so players learn the number.
    internal static class FleaCard
    {
        public const string ItemId = "flea_card";
        private const string GivenKey = "card_given";
        private const string ItemSprite = "item_card_flea";
        // The card as it's shown when read. Kept with the item sprites so the pixel art stays sharp when scaled.
        private const string CardArt = "card_flea";
        private const string BookId = "flea_card";
        // Matches the width of the game's own business cards.
        private const float CardScale = 2f;
        private const float CardArtWidth = 144f;
        private const float CardArtHeight = 72f;

        private static readonly List<ModItemDirectory> registered = new List<ModItemDirectory>();

        public static void Enable()
        {
            ModHook.OnModItemDirectoryInit += Register;
            // Directories that already exist were built before this mod was enabled.
            foreach (ModItemDirectory directory in Resources.FindObjectsOfTypeAll<ModItemDirectory>())
            {
                Register(directory);
            }
            ModHook.OnShutterOpenedEarly += GiveOnce;
            try
            {
                ModHelper.RegisterPaperBook(ModEntry.ModId, new PaperBookData
                {
                    id = BookId,
                    title = ModEntry.L("flea_card_name"),
                    background = CardArt,
                    width = CardArtWidth * CardScale,
                    height = CardArtHeight * CardScale,
                    designWidth = CardArtWidth,
                    designHeight = CardArtHeight,
                    zoomable = false,
                    // The game's own close button, like the X on its business cards.
                    chrome = PaperStarters.DefaultChrome(new Vector2(CardArtWidth, CardArtHeight), CardScale,
                        close: true, zoom: false, flip: false, pageIndicator: false),
                    pages = new List<PaperPageData> { new PaperPageData() }
                });
            }
            catch (Exception e)
            {
                ModEntry.Log.Error("Could not register the card's art: " + e);
            }
        }

        public static void Disable()
        {
            ModHook.OnModItemDirectoryInit -= Register;
            ModHook.OnShutterOpenedEarly -= GiveOnce;
            ModHelper.RemovePaperBooks(ModEntry.ModId);
            foreach (ModItemDirectory directory in registered)
            {
                if (directory != null && directory.factoryDictionary != null)
                {
                    directory.Remove(ItemId);
                }
            }
            registered.Clear();
        }

        private static void Register(ModItemDirectory directory)
        {
            if (directory == null || directory.factoryDictionary == null || directory.factoryDictionary.ContainsKey(ItemId))
            {
                return;
            }
            if (directory.Add(ItemId, () => Create(directory)))
            {
                registered.Add(directory);
            }
            else
            {
                ModEntry.Log.Warning("Item registration refused: " + ItemId);
            }
        }

        private static GameItem Create(ModItemDirectory directory)
        {
            GameItem item = directory.Empty().SetSpriteAndShapeFromMod(ModEntry.ModId, ItemSprite);
            item.name = ModEntry.L("flea_card_name");
            item.flavorText = ModEntry.L("flea_card_flavor");
            item.shortDescription = "";
            item.unitValue = 0L;
            item.canActivateSlotItemFunc = (_, _, _) => true;
            item.mayActivateSlotItemFunc = (_, _, _) => true;
            item.SetGameItemType("DOCUMENT");
            item.EnableTag("IMPORTANT_TAG");
            AudioHelper.InitCartonCard(item);
            item.onActivateSlotItemFunc = delegate { ToggleCard(); };
            ModHelper.SetSource(item, ModEntry.ModId, ModEntry.L("trader_name"));
            return item;
        }

        // Like the game's cards, using it again puts it away.
        private static void ToggleCard()
        {
            string id = PaperBookRegistry.FullId(ModEntry.ModId, BookId);
            if (PaperBookRegistry.IsOpen(id))
            {
                PaperBookRegistry.Close(id);
            }
            else
            {
                ProvideCloseSprite();
                PaperBookRegistry.Open(id);
            }
        }

        // The released game ships without its paper asset catalogue, so the close button can't find the
        // vanilla cross by name and draws a blue plate. The cross is loaded for the game's own cards, so
        // it's found by name and lent to this mod, where the paper UI looks next.
        private static void ProvideCloseSprite()
        {
            try
            {
                if (PaperAssets.Resolve(PaperAssets.SPRITE_CLOSE, ModEntry.ModId, quiet: true) != null)
                {
                    return;
                }
                LoadedMod mod = ModLoader.Mods.Find(m => m.Manifest.ID == ModEntry.ModId);
                Sprite cross = Array.Find(Resources.FindObjectsOfTypeAll<Sprite>(), sprite => sprite.name == PaperAssets.SPRITE_CLOSE);
                if (mod == null || cross == null)
                {
                    ModEntry.Log.Warning("The card's close button has no vanilla cross to use.");
                    return;
                }
                mod.PaperSprites[PaperAssets.SPRITE_CLOSE] = cross;
            }
            catch (Exception e)
            {
                ModEntry.Log.Warning("Could not find the card's close button art: " + e.Message);
            }
        }

        // The save remembers the card, so a run gets it once, and a run started before the mod gets it on its next morning.
        private static void GiveOnce()
        {
            try
            {
                if (ModHelper.GetModData(ModEntry.ModId, GivenKey) == "1" || !DirectoryMaster.Has<GameItem>(ItemId))
                {
                    return;
                }
                GameItem card = DirectoryMaster.Item(ItemId);
                if (PlayerStore.Instance.TryAddDirectSellingItemToTable(card, isOwend: true))
                {
                    ModHelper.SetModData(ModEntry.ModId, GivenKey, "1");
                }
                else
                {
                    GraphUtils.DestroyIfUnplaced(card, "front counter full");
                }
            }
            catch (Exception e)
            {
                ModEntry.Log.Error("Could not leave the trader's card: " + e);
            }
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TheFlea
{
    public class ModEntry : IMod
    {
        public const string ModId = "the_flea";

        private static readonly Dictionary<string, int> lastLines = new Dictionary<string, int>();

        internal static ModLog Log;
        internal static ConfigEntry<string> ServerUrl;
        internal static ConfigEntry<string> MarketCodeSetting;
        internal static ConfigEntry<int> CooldownDays;
        internal static ConfigEntry<bool> SellAnonymously;
        internal static ConfigEntry<bool> HideSellerNames;
        internal static MonoBehaviour Host;

        internal static string L(string key, params object[] args) => ModHelper.GetLocalized(ModId, key, args);

        // Picks one of a line's numbered variations (key_1, key_2...), avoiding the one used last time.
        internal static string Line(string key, params object[] args)
        {
            int count = LineCount(key);
            if (count == 0)
            {
                return L(key, args);
            }
            int pick = Random.Range(1, count + 1);
            if (count > 1 && lastLines.TryGetValue(key, out int last) && pick == last)
            {
                pick = pick % count + 1;
            }
            lastLines[key] = pick;
            return L($"{key}_{pick}", args);
        }

        // A translation sets its own variations. Lines it doesn't translate come from English.
        private static int LineCount(string key)
        {
            Dictionary<string, Dictionary<string, string>> files = ModLoader.Mods.Find(mod => mod.Manifest.ID == ModId)?.Localization;
            if (files == null)
            {
                return 0;
            }
            if (files.TryGetValue(L("lang_code"), out Dictionary<string, string> translated))
            {
                int count = CountLines(translated, key);
                if (count > 0 || translated.ContainsKey(key))
                {
                    return count;
                }
            }
            return files.TryGetValue("en", out Dictionary<string, string> english) ? CountLines(english, key) : 0;
        }

        private static int CountLines(Dictionary<string, string> lines, string key)
        {
            int count = 0;
            while (lines.ContainsKey($"{key}_{count + 1}"))
            {
                count++;
            }
            return count;
        }

        // Players who enter the same code share a market. Characters the server would reject are dropped,
        // so a typo in the settings can't stop the trader from coming.
        internal static string MarketCode()
        {
            return Identity.MarketCode(MarketCodeSetting.Value);
        }

        public void Init(ModManifest manifest)
        {
            Log = new ModLog(manifest);
            ModConfig config = ModHelper.GetConfig(ModId);
            ServerUrl = config.Define("server_url", "https://the-flea-market.throbbing-rice-1140.workers.dev", "Market", () => L("config_server_url"));
            MarketCodeSetting = config.Define("market_code", Identity.DefaultMarket, "Market", () => L("config_market_code"));
            CooldownDays = config.Define("cooldown_days", 1, "Market", () => L("config_cooldown"), min: 0, max: 7);
            SellAnonymously = config.Define("sell_anonymously", false, "Market", () => L("config_anonymous"));
            HideSellerNames = config.Define("hide_seller_names", false, "Market", () => L("config_hide_sellers"));
        }

        public void OnEnable()
        {
            GameObject host = new GameObject("TheFleaHost");
            Object.DontDestroyOnLoad(host);
            Host = host.AddComponent<CoroutineHost>();
            PhoneContact.RegisterDialogue();
            ModHook.OnGameLoadedLate += PhoneContact.Register;
            ModHook.OnGameLoadedLate += MintCatalogue.LoadInBackground;
            ModHook.OnNewGameStarted += OnNewGame;
            ModHook.OnGenerateCustomerVeryLate += TraderVisit.OnCustomersGenerated;
            ModHook.OnCreateTooltipLate += ItemCodec.AddSellerLine;
            FleaCard.Enable();
            SelfTest.Register();
        }

        public void OnDisable()
        {
            ModHook.OnGameLoadedLate -= PhoneContact.Register;
            ModHook.OnGameLoadedLate -= MintCatalogue.LoadInBackground;
            ModHook.OnNewGameStarted -= OnNewGame;
            ModHook.OnGenerateCustomerVeryLate -= TraderVisit.OnCustomersGenerated;
            ModHook.OnCreateTooltipLate -= ItemCodec.AddSellerLine;
            FleaCard.Disable();
            SelfTest.Unregister();
        }

        private static void OnNewGame(string scenarioId)
        {
            PhoneContact.Register();
            MintCatalogue.LoadInBackground();
        }
    }

    internal class CoroutineHost : MonoBehaviour
    {
    }
}

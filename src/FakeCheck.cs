using System;
using System.Collections.Generic;
using System.Linq;

namespace TheFlea
{
    // The trader looks over what's handed in and catches most, but not all, items that aren't what they claim.
    internal static class FakeCheck
    {
        private const int MissPercent = 10;

        // Fixed per item, so reloading the morning save or re-placing the item can't change the result.
        private static bool IsMissed(int uniqueId) => BatchStore.Roll(uniqueId, 100) < MissPercent;

        public static bool WillCatch(ItemFeature feature)
        {
            GameItem item = feature.GetParentItem();
            return item != null && GeneralHelper.IsItemOwned(item) && !IsMissed(feature.parentItemUniqueId);
        }

        // Uses the game's own rule for what a customer can expose, without its customer reaction:
        // that reaction costs reputation and has no dialogue for some feature categories.
        public static void Inspect(StoreClient client, GameItem item, bool speak)
        {
            string name = item.GetDisplayName();
            bool caught = false;
            foreach (ItemFeature feature in item.itemFeatures.ToList())
            {
                if (client.CanClientExposeThisFeature(feature))
                {
                    feature.ExposeFeature(lowkey: true, playBad: true);
                    caught = true;
                }
            }
            if (caught && speak)
            {
                Dialogue line = new Dialogue().SetText(client.displayName, ModEntry.Line("visit_caught", name));
                line.priority = Dialogue.IMPORTANT_PRIORITY;
                DialogUIManager.Instance.StartDialogue(line);
            }
        }

        public static void InspectAll(StoreClient client, GameItem item)
        {
            foreach (GameItem part in item.FindAllChildItems().Append(item).Distinct())
            {
                Inspect(client, part, speak: false);
            }
        }

        // Feature data isn't in the payload, so authenticity travels as a mark the buyer's game rebuilds from.
        // An exposed feature is sent as what it really is, so it arrives already known.
        public static List<ItemMark> Describe(GameItem item, List<SaveItemNode> nodes)
        {
            List<ItemMark> marks = new List<ItemMark>();
            foreach (GameItem part in item.FindAllChildItems().Append(item).Distinct())
            {
                int node = nodes.FindIndex(n => n.uniqueId == part.uniqueId);
                ItemFeature feature = part.itemFeatures.FirstOrDefault(f => f.useCondition && IsAuthenticityCategory(f.GetCategory()));
                if (node >= 0 && feature != null)
                {
                    marks.Add(new ItemMark
                    {
                        node = node,
                        category = feature.GetCategory(),
                        real = feature.realCondition.identifier,
                        shown = feature.IsExposed() ? feature.realCondition.identifier : feature.fakeCondition.identifier
                    });
                }
            }
            return marks;
        }

        // Marks come from other players, so each must match its item and a combination the game itself has.
        public static void Apply(ItemMark mark, GameItem item)
        {
            ItemFeature feature = Build(mark, item.identifier);
            if (feature == null)
            {
                return;
            }
            foreach (ItemFeature existing in item.itemFeatures.Where(f => f.GetCategory() == mark.category).ToList())
            {
                item.RemoveItemFeature(existing);
            }
            item.AddItemFeature(feature);
            if (item.identifier == StampId && mark.shown == GenuineStamp && item.state.dict.TryGetValue("STAMP_AMOUNT", out TagState amount) && amount.GetInt() > 0)
            {
                item.name = LocHelper.GetLocalizedMechanic("ins_stamp_name", amount.GetInt());
            }
        }

        private const string CigaretteCategory = "CATEGORY_GENUINE_CIGARETTE";
        private const string StampCategory = "CATEGORY_GENUINE_STAMP";
        private const string InjectorCategory = "CATEGORY_GENUINE_INJECTOR";
        private const string CigaretteId = "packet_red_cigarette";
        private const string StampId = "food_stamp";
        private const string InjectorId = "large_purple_injector";
        private const string GenuineCigarette = "genuineCigarette";
        private const string CounterfeitCigarette = "counterfeitCigarette";
        private const string GenuineStamp = "genuineStamp";
        private const string CounterfeitStamp = "counterfeitStamp";

        private static bool IsAuthenticityCategory(string category)
        {
            return category == CigaretteCategory || category == StampCategory || category == InjectorCategory;
        }

        private static ItemFeature Build(ItemMark mark, string itemId)
        {
            switch (mark.category)
            {
                case CigaretteCategory when itemId == CigaretteId:
                    if (mark.shown == GenuineCigarette)
                    {
                        return mark.real == GenuineCigarette ? ItemFeatureList.CigaretteAuthenticity()
                            : mark.real == CounterfeitCigarette ? ItemFeatureList.CigaretteFakeAuthenticity() : null;
                    }
                    if (mark.shown == CounterfeitCigarette)
                    {
                        return mark.real == CounterfeitCigarette ? ItemFeatureList.CigaretteCounterfeit()
                            : mark.real == GenuineCigarette ? ItemFeatureList.CigaretteFakeCounterfeit() : null;
                    }
                    return null;
                case StampCategory when itemId == StampId:
                    if (mark.shown == GenuineStamp)
                    {
                        return mark.real == GenuineStamp ? ItemFeatureList.StampAuthenticity()
                            : mark.real == CounterfeitStamp ? ItemFeatureList.StampFakeAuthenticity() : null;
                    }
                    return mark.shown == CounterfeitStamp && mark.real == CounterfeitStamp ? ItemFeatureList.StampCounterfeit() : null;
                case InjectorCategory when itemId == InjectorId:
                    ItemFeatureList.InjectorConditionType? shown = InjectorType(mark.shown);
                    ItemFeatureList.InjectorConditionType? real = InjectorType(mark.real);
                    return shown != null && real != null ? ItemFeatureList.InjectorFeature(shown.Value, real.Value) : null;
                default:
                    return null;
            }
        }

        private static ItemFeatureList.InjectorConditionType? InjectorType(string condition)
        {
            switch (condition)
            {
                case "genuineInjector": return ItemFeatureList.InjectorConditionType.Genuine;
                case "counterfeitInjector": return ItemFeatureList.InjectorConditionType.Counterfeit;
                case "expiredInjector": return ItemFeatureList.InjectorConditionType.Expired;
                case "unusableInjector": return ItemFeatureList.InjectorConditionType.Unusable;
                default: return null;
            }
        }
    }
}

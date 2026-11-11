using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TheFlea
{
    internal static class PhoneContact
    {
        // 3532 spells FLEA on a phone keypad.
        public const long Number = 3532L;
        public const string DialogueId = "flea_trader";
        private const string PlayerTitle = "PLAYER";
        private const string ChoiceItemId = "$";
        private const int WantedBubbles = 6;

        public static void RegisterDialogue()
        {
            PhoneClientList.DialogFuncs[DialogueId] = BuildCall;
        }

        public static void Register()
        {
            Dictionary<long, StorePhoneClient> contacts = PlayerStore.Instance?.PhoneClientDict;
            if (contacts == null)
            {
                return;
            }
            if (contacts.TryGetValue(Number, out StorePhoneClient contact))
            {
                if (contact?.dialogFuncId != DialogueId)
                {
                    ModEntry.Log.Warning($"Phone number {Number} is already used by another contact.");
                    return;
                }
            }
            else
            {
                contact = new StorePhoneClient
                {
                    phoneClientType = StorePhoneClient.PhoneClientType.None,
                    phoneState = StorePhoneClient.PhoneState.Regular,
                    dialogFuncId = DialogueId,
                    locID = DialogueId,
                    dialedBefore = true,
                    alias = new List<string> { TraderVisit.ClientId }
                };
                contacts.Add(Number, contact);
            }
            contact.displayName = ModEntry.L("trader_name");
            contact.cooldownDuration = ModEntry.CooldownDays.Value;
        }

        public static void UseService()
        {
            StorePhoneClient contact = StorePhoneClient.GetPhoneClientByNumber(Number);
            if (contact != null && ModEntry.CooldownDays.Value > 0)
            {
                contact.cooldownDuration = ModEntry.CooldownDays.Value;
                contact.UseService();
            }
        }

        private static Dialogue BuildCall(StorePhoneClient contact)
        {
            string name = ModEntry.L("trader_name");
            if (contact.phoneState == StorePhoneClient.PhoneState.Cooldown)
            {
                return PhoneDialogList.GenericCooldown(name);
            }
            List<string> categories = new List<string> { "" };
            categories.AddRange(TypeHelper.MAJOR_TYPES_LIST);
            AddBubbles();
            return BuildPage(name, categories, 0);
        }

        // The phone indexes a fixed list of choice bubbles and throws past its end, so more
        // choices need more bubbles. Clones keep the click handler wired up in the scene.
        private static void AddBubbles()
        {
            List<DialogChoiceBubble> bubbles = DialoguePhoneUIManager.Instance.dialogChoiceBubbles;
            if (bubbles.Count == 0 || bubbles.Count >= WantedBubbles)
            {
                return;
            }
            RectTransform last = (RectTransform)bubbles[bubbles.Count - 1].transform;
            Vector2 step = bubbles.Count > 1
                ? last.anchoredPosition - ((RectTransform)bubbles[bubbles.Count - 2].transform).anchoredPosition
                : new Vector2(0f, -last.rect.height);
            bool hasLayout = last.parent.GetComponent<LayoutGroup>() != null;
            while (bubbles.Count < WantedBubbles)
            {
                DialogChoiceBubble clone = UnityEngine.Object.Instantiate(bubbles[bubbles.Count - 1], last.parent);
                clone.index = bubbles.Count;
                clone.gameObject.SetActive(false);
                if (!hasLayout)
                {
                    RectTransform previous = (RectTransform)bubbles[bubbles.Count - 1].transform;
                    ((RectTransform)clone.transform).anchoredPosition = previous.anchoredPosition + step;
                }
                bubbles.Add(clone);
            }
        }

        // Categories are paged to fit the choice bubbles, leaving room for More and Never mind.
        private static Dialogue BuildPage(string name, List<string> categories, int start)
        {
            int bubbles = DialoguePhoneUIManager.Instance.dialogChoiceBubbles.Count;
            int slots = Math.Max(1, bubbles - 2);
            int end = Math.Min(start + slots, categories.Count);
            Dialogue page = new Dialogue().SetText(name, ModEntry.Line("phone_pick"));
            int arrival = BatchStore.ArrivalDay();
            int days = arrival - StoreStation.Instance.dayCounter;
            for (int i = start; i < end; i++)
            {
                string category = categories[i];
                string label = category == "" ? ModEntry.L("phone_any") : TypeHelper.GetTypeDisplayName(category);
                Dialogue choice = new Dialogue().SetText(PlayerTitle, label);
                string coming = days > 1 ? ModEntry.Line("phone_coming_late", days) : ModEntry.Line("phone_coming");
                choice.NextDialogue().SetText(name, coming).SetEndAction(delegate
                {
                    HangUp();
                    BatchStore.SaveCall(category, arrival);
                    UseService();
                });
                page.AddChoice(DirectoryMaster.Item(ChoiceItemId), "", "", choice);
            }
            if (end < categories.Count)
            {
                Dialogue more = new Dialogue().SetText(PlayerTitle, ModEntry.L("phone_more"));
                more.SetNextDialogue(BuildPage(name, categories, end));
                page.AddChoice(DirectoryMaster.Item(ChoiceItemId), "", "", more);
            }
            Dialogue cancel = new Dialogue().SetText(PlayerTitle, ModEntry.L("phone_cancel"));
            cancel.NextDialogue().SetText(name, ModEntry.Line("phone_bye")).SetEndAction(HangUp);
            page.AddChoice(DirectoryMaster.Item(ChoiceItemId), "", "", cancel);
            return page;
        }

        private static void HangUp() => PhoneUIManager.Instance.OnPhoneDialogEnd();
    }
}

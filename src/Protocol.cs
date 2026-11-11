using System;
using System.Collections.Generic;

namespace TheFlea
{
    [Serializable]
    internal class PendingItem
    {
        public string item_id;
        public int count;
        public List<string> types = new List<string>();
        public string payload;
        public long value;
    }

    [Serializable]
    internal class PendingBatch
    {
        public string batch_id;
        public string market;
        public List<PendingItem> items = new List<PendingItem>();
    }

    [Serializable]
    internal class SalesRequest
    {
        public string market;
        public string client;
        public string batch_id;
        public List<PendingItem> items;
    }

    [Serializable]
    internal class PriceEntry
    {
        public string item_id;
        public float sell_multiplier;
        public float buy_multiplier;
    }

    [Serializable]
    internal class SalesResponse
    {
        public string batch_id;
        public List<PriceEntry> prices = new List<PriceEntry>();
    }

    [Serializable]
    internal class Offer
    {
        public string listing_id;
        public string item_id;
        public float buy_multiplier;
        public string payload;
        public bool minted;
        public double value;
        public List<string> types;
    }

    [Serializable]
    internal class OffersResponse
    {
        public List<Offer> offers = new List<Offer>();
        public Dictionary<string, float> categories;
        public float fee;
    }

    [Serializable]
    internal class OffersRequest
    {
        public string market;
        public string client;
        public string category;
        public int limit;
        public List<string> categories;
        // Real listings bought on earlier visits and not reported yet.
        public List<string> bought;
    }

    // The last category prices and fee a market sent, kept to pay batches when the server can't.
    [Serializable]
    internal class MarketRates
    {
        public string market;
        public Dictionary<string, float> categories;
        public float fee;
        // Unix seconds, UTC.
        public long saved_at;
    }

    [Serializable]
    internal class ItemEnvelope
    {
        public string lang;
        public string seller;
        public string state;
        public List<ItemMark> marks;
    }

    // What a part of the item shows and really is. "node" is its position in the state's item list.
    [Serializable]
    internal class ItemMark
    {
        public int node;
        public string category;
        public string shown;
        public string real;
    }
}

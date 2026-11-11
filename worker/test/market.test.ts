import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { after, before, beforeEach, test } from "node:test";
import { getPlatformProxy } from "wrangler";
import { handle, network } from "../src/index.ts";
import { BadRequest, DEFAULT_CONFIG, Market } from "../src/market.ts";

const alice = "a".repeat(32);
const bob = "b".repeat(32);
const HOUR = 3600;
const DAY = 86400;

let proxy;
let db;
let rowsRead = 0;
let rowsWritten = 0;

// Every query goes through batch(), so this sees what each operation costs on the free plan.
const countingDb = {
  prepare: (sql) => db.prepare(sql),
  batch: async (statements) => {
    const results = await db.batch(statements);
    for (const result of results) {
      rowsRead += result.meta.rows_read;
      rowsWritten += result.meta.rows_written;
    }
    return results;
  },
};

before(async () => {
  proxy = await getPlatformProxy({ persist: false });
  db = proxy.env.DB;
  const folder = new URL("../migrations/", import.meta.url);
  const schema = readdirSync(folder).sort().map((name) => readFileSync(new URL(name, folder), "utf8")).join("\n");
  await db.batch(schema.split(";").map((sql) => sql.trim()).filter(Boolean).map((sql) => db.prepare(sql)));
});

after(async () => {
  await proxy.dispose();
});

beforeEach(async () => {
  await db.batch(["listings", "receipts", "trades", "category_trades", "batches", "seller_quota", "banned_sellers"].map((table) => db.prepare(`DELETE FROM ${table}`)));
  await db.prepare("UPDATE storage SET listing_bytes = 0").run();
});

const NO_LIMITS = { maxUnitsPerItem: 9999, maxLiveListings: Infinity, maxListingsPerDay: Infinity, maxUnitsPerDay: Infinity, minRealShare: 1, maxRealShare: 1, maxSellerShare: 1 };

function setup(change = {}, database = countingDb) {
  const clock = { now: 1000 };
  const market = new Market(database, { ...DEFAULT_CONFIG, ...NO_LIMITS, mintOffers: false, ...change }, () => clock.now);
  const f = {
    clock,
    market,
    post: (client, batchId, items, code, source) => market.postSales({ client, batch_id: batchId, items, market: code }, source),
    offers: async (client, category = "", limit = 30, code, bought) => (await market.postOffers({ market: code, client, category, limit, bought })).offers,
    // Reports purchases with an offers request from a market with nothing in it, so it takes nothing else.
    report: (client, bought) => market.postOffers({ market: "nowhere", client, bought }),
    price: async (itemId, code) => market["price"](itemId, await f.totals(itemId, code), clock.now),
    totals: async (itemId, code) => {
      const [result] = await db.batch([market["trades"]("item", code ?? "0", [itemId])]);
      return result.results[0];
    },
    categoryTotals: async (type, code) => {
      const [result] = await db.batch([market["trades"]("category", code ?? "0", [type])]);
      return result.results[0];
    },
    buyAll: async (itemId, count) => {
      await f.post(alice, "stock", [sale(itemId, count)]);
      clock.now += DAY + 1;
      const ids = (await f.offers(bob)).map((offer) => offer.listing_id);
      await f.report(bob, ids);
      return f.price(itemId);
    },
  };
  return f;
}

function sale(itemId, count = 1, types = ["MATERIAL"]) {
  return { item_id: itemId, count, types, payload: '{"saveItems":[]}', value: 100 };
}

async function storedBytes() {
  return (await db.prepare("SELECT listing_bytes FROM storage").first()).listing_bytes;
}

async function sellerOf() {
  const { results } = await db.prepare("SELECT id, seller FROM listings").all();
  const sellers = new Map(results.map((row) => [row.id, row.seller]));
  return (offer) => sellers.get(offer.listing_id);
}

async function count(table) {
  return (await db.prepare(`SELECT COUNT(*) AS total FROM ${table}`).first()).total;
}

// Every listing left in receipts, as { id, client, market, item_id, units, types, created_at, sent_at }.
async function receipted() {
  const { results } = await db.prepare("SELECT client, market, sent_at, listings FROM receipts").all();
  return results.flatMap((row) =>
    Object.entries(JSON.parse(row.listings)).map(([id, [item_id, units, types, created_at]]) => ({ id, client: row.client, market: row.market, item_id, units, types, created_at, sent_at: row.sent_at })),
  );
}

async function expiries() {
  const { results } = await db.prepare("SELECT item_id, (expires_at - created_at) / 86400.0 AS days FROM listings ORDER BY item_id").all();
  return Object.fromEntries(results.map((row) => [row.item_id, row.days]));
}

function near(got, want, tolerance) {
  assert.ok(Math.abs(got - want) <= tolerance, `got ${got}, want ${want}`);
}

test("more sales lower the price", async () => {
  const f = setup();
  const first = (await f.post(alice, "b1", [sale("scrap", 1)])).prices[0];
  const second = (await f.post(alice, "b2", [sale("scrap", 100)])).prices[0];
  assert.ok(second.buy_multiplier < first.buy_multiplier);
  assert.ok(first.sell_multiplier < first.buy_multiplier);
});

test("price never falls below the floor", async () => {
  const f = setup();
  assert.equal((await f.post(alice, "b1", [sale("scrap", 9999)])).prices[0].buy_multiplier, 0.5);
});

test("a sale's effect halves every half-life", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("scrap", 8)]);
  near((await f.price("scrap")).buy_multiplier, Math.exp(-8 / 20), 0.0001);
  f.clock.now += 2 * HOUR;
  near((await f.price("scrap")).buy_multiplier, Math.exp(-4 / 20), 0.0001);
  f.clock.now += 2 * HOUR;
  near((await f.price("scrap")).buy_multiplier, Math.exp(-2 / 20), 0.0001);
});

test("a new sale adds to what's left of the old one", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("scrap", 8)]);
  f.clock.now += 2 * HOUR;
  const [price] = (await f.post(alice, "b2", [sale("scrap", 1)])).prices;
  near(price.buy_multiplier, Math.exp(-5 / 20 - 5 / 300), 0.0001);
  near((await f.totals("scrap")).net, -5, 0.0001);
  near((await f.categoryTotals("MATERIAL")).net, -5, 0.0001);
  assert.equal((await f.totals("scrap")).updated_at, f.clock.now);
});

test("old sales are forgotten", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("scrap", 100)]);
  f.clock.now += DAY;
  near((await f.post(alice, "b2", [sale("scrap", 1)])).prices[0].buy_multiplier, Math.exp(-1 / 20 - 1 / 300), 0.002);
});

test("buying a stack long after its sale doesn't cause a rebound", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 60, ["FOOD"])]);
  f.clock.now += 3 * HOUR;
  await f.report(bob, (await f.offers(bob)).map((offer) => offer.listing_id));
  near((await f.totals("bread")).net, 60 * 0.25, 0.0001);
  near((await f.categoryTotals("FOOD")).net, 60 * 0.25, 0.0001);
  f.clock.now += 6 * HOUR;
  near((await f.price("bread")).buy_multiplier, Math.exp((15 / 8) / 20), 0.0001);
});

test("listings bought at once leave a little demand behind", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 4)]);
  await f.report(bob, (await f.offers(bob)).map((offer) => offer.listing_id));
  near((await f.totals("bread")).net, 1, 0.0001);
});

test("a purchase report counts the sale's age when reported, in the item and each of its categories", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("fork", 8, ["FOOD", "TOOL"])]);
  f.clock.now += HOUR;
  const [offer] = await f.offers(bob);
  f.clock.now += 2 * HOUR;
  await f.report(bob, [offer.listing_id]);
  const faded = 8 * 0.5 ** (3 / 2);
  for (const totals of [await f.totals("fork"), await f.categoryTotals("FOOD"), await f.categoryTotals("TOOL")]) {
    near(totals.net, -faded + faded + 8 * 0.25, 0.0001);
    near(totals.volume, 8 * 0.5 ** (3 / 12) + 8, 0.0001);
    assert.equal(totals.updated_at, f.clock.now);
  }
});

test("nothing sold yet starts at one", async () => {
  const f = setup();
  const price = await f.price("never_sold");
  assert.equal(price.buy_multiplier, 1);
  assert.equal(price.sell_multiplier, 0.9);
});

test("purchases raise the price above one", async () => {
  const f = setup();
  const price = await f.buyAll("bread", 24);
  near(price.buy_multiplier, Math.exp(6 / 20), 0.001);
  assert.ok(price.sell_multiplier > 1);
});

test("price never rises above the ceiling", async () => {
  const f = setup();
  assert.equal((await f.buyAll("bread", 9999)).buy_multiplier, 1.5);
});

test("reporting a purchase twice counts once", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 4)]);
  const id = (await f.offers(bob))[0].listing_id;
  await f.report(bob, [id]);
  await f.report(bob, [id]);
  near((await f.totals("bread")).net, 1, 0.0001);
  assert.equal((await f.totals("bread")).volume, 8);
  assert.equal(await count("listings"), 0);
  assert.equal(await count("receipts"), 0);
});

test("a client can't report another client's receipt", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 4)]);
  const id = (await f.offers(bob))[0].listing_id;
  await f.report(alice, [id]);
  await f.report("c".repeat(32), [id]);
  assert.equal((await f.totals("bread")).net, -4);
  assert.equal(await count("receipts"), 1);
  await f.report(bob, [id]);
  near((await f.totals("bread")).net, 1, 0.0001);
});

test("the same offers request retried counts its purchases once", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 4, ["FOOD"])]);
  const id = (await f.offers(bob))[0].listing_id;
  await f.post(alice, "b2", [sale("cheese", 1, ["FOOD"])]);
  const request = { client: bob, category: "FOOD", limit: 8, categories: ["FOOD"], bought: [id] };
  const first = await f.market.postOffers(request);
  const second = await f.market.postOffers(request);
  assert.deepEqual(first.offers.map((offer) => offer.item_id), ["cheese"]);
  assert.equal(second.offers.length, 0);
  near((await f.totals("bread")).net, 1, 0.0001);
  assert.equal((await f.totals("bread")).volume, 8);
  near(first.categories.FOOD, second.categories.FOOD, 0.0001);
  await Promise.all([f.market.postOffers(request), f.market.postOffers(request)]);
  assert.equal((await f.totals("bread")).volume, 8);
});

test("the same request sent twice at once counts its purchases once", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 4, ["FOOD"])]);
  const id = (await f.offers(bob))[0].listing_id;
  const request = { client: bob, bought: [id] };
  await Promise.all([f.market.postOffers(request), f.market.postOffers(request)]);
  near((await f.totals("bread")).net, 1, 0.0001);
  assert.equal((await f.totals("bread")).volume, 8);
  near((await f.categoryTotals("FOOD")).net, 1, 0.0001);
});

test("an expired receipt counts nothing and is cleaned up", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 4), sale("cheese", 4)]);
  const offers = await f.offers(bob);
  const bread = offers.find((offer) => offer.item_id === "bread").listing_id;
  const cheese = offers.find((offer) => offer.item_id === "cheese").listing_id;
  f.clock.now += 14 * DAY + 1;
  await f.report(bob, [bread]);
  assert.deepEqual(await f.totals("bread"), { name: "bread", net: -4, volume: 4, updated_at: 1000 });
  f.clock.now -= 2;
  await f.post(alice, "b2", [sale("fresh")]);
  await f.offers(bob);
  f.clock.now += 2;
  await f.market.prune();
  assert.deepEqual((await receipted()).map((row) => row.item_id), ["fresh"]);
  assert.equal(await count("receipts"), 1);
  assert.equal(await f.totals("cheese"), undefined);
  await f.report(bob, [cheese]);
  assert.equal(await f.totals("cheese"), undefined);
});

test("an ID repeated in one report counts once", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 4, ["FOOD"])]);
  const id = (await f.offers(bob))[0].listing_id;
  await f.report(bob, [id, id, id]);
  near((await f.totals("bread")).net, 1, 0.0001);
  assert.equal((await f.totals("bread")).volume, 8);
  near((await f.categoryTotals("FOOD")).net, 1, 0.0001);
  assert.equal((await f.categoryTotals("FOOD")).volume, 8);
});

test("an offer that isn't reported as bought doesn't count as a purchase", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 1)]);
  await f.offers(bob);
  await f.report(bob, []);
  assert.equal((await f.totals("bread")).net, -1);
});

test("busy items move less than quiet ones", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("busy", 9999)]);
  f.clock.now += 2 * DAY;
  await f.post(alice, "b2", [sale("busy", 10), sale("quiet", 10)]);
  near((await f.price("busy")).buy_multiplier, Math.exp(-10 / ((634.9 * 2) / 12)), 0.005);
  near((await f.price("quiet")).buy_multiplier, 0.6065, 0.001);
});

test("old volume is forgotten", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 9999)]);
  f.clock.now += 30 * DAY;
  await f.post(alice, "b2", [sale("bread", 10)]);
  near((await f.price("bread")).buy_multiplier, 0.6065, 0.001);
});

test("a repeated batch is not counted twice", async () => {
  const f = setup();
  const first = await f.post(alice, "b1", [sale("scrap", 10)]);
  const second = await f.post(alice, "b1", [sale("scrap", 10)]);
  assert.deepEqual(second, first);
  assert.equal(await count("listings"), 1);
  assert.equal(await count("trades"), 1);
  assert.equal((await f.totals("scrap")).volume, 10);
  assert.equal((await f.categoryTotals("MATERIAL")).volume, 10);
});

test("selling an item lowers others in its category, less than itself", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 5, ["FOOD"])]);
  const [cheese] = (await f.post(alice, "b2", [sale("cheese", 1, ["FOOD"])])).prices;
  near(cheese.buy_multiplier, Math.exp(-1 / 20 - 6 / 300), 0.0001);
  const [bread] = (await f.post(alice, "b3", [sale("bread", 1, ["FOOD"])])).prices;
  near(bread.buy_multiplier, Math.exp(-6 / 20 - 7 / 300), 0.0001);
});

test("other categories are untouched", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 20, ["FOOD"])]);
  const [knife] = (await f.post(alice, "b2", [sale("knife", 1, ["TOOL"])])).prices;
  near(knife.buy_multiplier, Math.exp(-1 / 20 - 1 / 300), 0.0001);
});

test("an item with several categories gets their average", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 40, ["FOOD"])]);
  const [fork] = (await f.post(alice, "b2", [sale("fork", 1, ["FOOD", "TOOL"])])).prices;
  near(fork.buy_multiplier, Math.exp(-1 / 20 + (-41 / 300 + -1 / 300) / 2), 0.0001);
});

test("an offer's price includes its category", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 20, ["FOOD"]), sale("cheese", 1, ["FOOD"])]);
  const offers = await f.offers(bob, "FOOD");
  const cheese = offers.find((offer) => offer.item_id === "cheese");
  near(cheese.buy_multiplier, Math.exp(-1 / 20 - 21 / 300), 0.0001);
});

test("buying raises the whole category once, in every category of the item", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("fork", 4, ["FOOD", "TOOL"])]);
  f.clock.now += DAY + 1;
  const id = (await f.offers(bob))[0].listing_id;
  await f.report(bob, [id]);
  await f.report(bob, [id]);
  near((await f.categoryTotals("FOOD")).net, 1, 0.01);
  near((await f.categoryTotals("TOOL")).net, 1, 0.01);
  await f.post(alice, "b2", [{ ...sale("knife", 1, ["TOOL"]), payload: "x" }]);
  const offer = (await f.offers(bob, "TOOL"))[0];
  near(offer.buy_multiplier, Math.exp(-1 / 20), 0.001);
});

test("the merged price stays between the floor and the ceiling", async () => {
  const f = setup();
  assert.equal((await f.post(alice, "b1", [sale("bread", 9999, ["FOOD"])])).prices[0].buy_multiplier, 0.5);
  await f.post(alice, "b2", [sale("wine", 2000, ["BREVAGE"])]);
  f.clock.now += DAY + 1;
  await f.report(bob, (await f.offers(bob, "BREVAGE")).map((offer) => offer.listing_id));
  await f.post(alice, "b3", [{ ...sale("beer", 1, ["BREVAGE"]) }]);
  assert.equal((await f.offers(bob, "BREVAGE"))[0].buy_multiplier, 1.5);
});

test("category supply is separate in each market", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 20, ["FOOD"])], "friends");
  const [cheese] = (await f.post(alice, "b2", [sale("cheese", 1, ["FOOD"])])).prices;
  near(cheese.buy_multiplier, Math.exp(-1 / 20 - 1 / 300), 0.0001);
});

test("only the game's categories are kept, major ones first, five at most", async () => {
  const f = setup();
  const types = ["RARE", "not safe!", "MADE_UP", "LOWTECH", "FOOD", "TREAT", "FRESH_FOOD", "PROCESSED_FOOD", "TOOL"];
  const response = await f.post(alice, "b1", [sale("bread", 1, types)]);
  assert.equal(response.listed, 1);
  assert.deepEqual((await f.offers(bob))[0].types, ["FOOD", "TOOL", "RARE", "LOWTECH", "TREAT"]);
  assert.equal(await count("category_trades"), 5);
});

test("a category the game doesn't have is refused", async () => {
  const f = setup();
  await assert.rejects(f.market.postOffers({ client: bob, category: "DRINK" }), BadRequest);
});

test("an item without categories is priced on its own", async () => {
  const f = setup();
  const [bread] = (await f.post(alice, "b1", [sale("bread", 1, [])])).prices;
  near(bread.buy_multiplier, Math.exp(-1 / 20), 0.0001);
  assert.equal((await f.offers(bob))[0].buy_multiplier, bread.buy_multiplier);
});

test("the same batch sent twice at once is counted once", async () => {
  const f = setup();
  const [first, second] = await Promise.all([f.post(alice, "b1", [sale("scrap", 10)]), f.post(alice, "b1", [sale("scrap", 10)])]);
  assert.deepEqual(second, first);
  assert.equal(await count("listings"), 1);
  assert.equal((await f.totals("scrap")).volume, 10);
});

test("an oversized payload is priced but not listed", async () => {
  const f = setup();
  const response = await f.post(alice, "b1", [{ ...sale("scrap"), payload: "x".repeat(64 * 1024 + 1) }]);
  assert.equal(response.listed, 0);
  assert.equal(response.prices.length, 1);
});

test("payload size is measured in bytes", async () => {
  const f = setup();
  const response = await f.post(alice, "b1", [{ ...sale("scrap"), payload: "酒".repeat(30000) }]);
  assert.equal(response.listed, 0);
});

test("a full batch of large listings fits", async () => {
  const f = setup();
  const items = Array.from({ length: 20 }, (_, index) => ({ ...sale(`item${index}`, 1, ["FOOD", "TOOL"]), payload: '"'.repeat(60000) }));
  assert.equal((await f.post(alice, "b1", items)).listed, 20);
  assert.equal(await count("listings"), 20);
});

test("unknown modded IDs are accepted", async () => {
  const f = setup();
  for (const [index, id] of ["be_some_modded_wine", "模組_酒.1", "mod:item$2"].entries()) {
    assert.equal((await f.post(alice, `b${index}`, [sale(id, 1, ["ALCOHOL"])])).listed, 1);
  }
});

test("unreadable items are skipped", async () => {
  const f = setup();
  const response = await f.post(alice, "b1", [
    "not an object",
    { item_id: "bad id with spaces", count: 1 },
    { item_id: "scrap", count: 1.5, types: ["MATERIAL"], payload: "x", value: 100 },
  ]);
  assert.equal(response.listed, 1);
  assert.equal(response.prices.length, 1);
  assert.equal((await f.totals("scrap")).volume, 1);
});

test("an item that can't be listed gets a price but doesn't move the market", async () => {
  const f = setup();
  for (const [index, item] of [
    { item_id: "scrap", count: 9000, types: [1], payload: "x", value: 100 },
    { item_id: "scrap", count: 9000, types: ["FOOD"], value: 100 },
    { item_id: "scrap", count: 9000, types: ["FOOD"], payload: "x".repeat(64 * 1024 + 1), value: 100 },
  ].entries()) {
    const response = await f.post(alice, `b${index}`, [item]);
    assert.equal(response.listed, 0);
    assert.deepEqual(response.prices, [{ item_id: "scrap", sell_multiplier: 0.9, buy_multiplier: 1 }]);
  }
  assert.equal(await count("trades"), 0);
  assert.equal(await count("category_trades"), 0);
});

test("items worth nothing are filtered out", async () => {
  const f = setup({ mintOffers: true });
  for (const [index, value] of [0, -5, undefined, "9", null].entries()) {
    const response = await f.post(alice, `b${index}`, [{ ...sale("scrap", 500, ["FOOD"]), value }]);
    assert.equal(response.listed, 0);
    assert.equal(response.prices[0].buy_multiplier, 1);
  }
  const infinite = await f.market.postSales(JSON.parse(`{"client":"${alice}","batch_id":"b9","items":[{"item_id":"scrap","count":500,"types":["FOOD"],"payload":"x","value":1e999}]}`));
  assert.equal(infinite.listed, 0);
  assert.equal(await count("listings"), 0);
  assert.equal(await count("trades"), 0);
  assert.equal(await count("seller_quota"), 0);
});

test("a stack over the unit limit counts as one", async () => {
  const f = setup({ maxUnitsPerItem: 999 });
  await f.post(alice, "b1", [sale("scrap", 1000), sale("bread", 999)]);
  assert.equal((await f.totals("scrap")).volume, 1);
  assert.equal((await f.totals("bread")).volume, 999);
});

test("an empty batch isn't remembered", async () => {
  const f = setup();
  assert.deepEqual(await f.post(alice, "b1", []), { batch_id: "b1", listed: 0, prices: [] });
  assert.equal(await count("batches"), 0);
});

test("sellers do not see their own listings", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("scrap")]);
  assert.equal((await f.offers(alice)).length, 0);
  assert.equal((await f.offers(bob)).length, 1);
});

test("allow self shows own listings", async () => {
  const f = setup({ allowSelf: true });
  await f.post(alice, "b1", [sale("scrap")]);
  assert.equal((await f.offers(alice)).length, 1);
});

test("category filter", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 1, ["FOOD"]), sale("knife", 1, ["WEAPON", "TOOL"])]);
  const food = await f.offers(bob, "FOOD");
  assert.deepEqual(food.map((offer) => offer.item_id), ["bread"]);
  assert.equal((await f.offers(bob, "ARMOR")).length, 0);
});

test("repeated types don't break a listing", async () => {
  const f = setup();
  assert.equal((await f.post(alice, "b1", [sale("bread", 1, ["FOOD", "FOOD"])])).listed, 1);
  assert.deepEqual((await f.offers(bob, "FOOD"))[0].types, ["FOOD"]);
});

test("a sent listing leaves the market and can't be sent to anyone else", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("scrap", 3, ["MATERIAL", "JUNK"])]);
  const [offer] = await f.offers(bob);
  assert.equal(await count("listings"), 0);
  assert.equal((await f.offers("c".repeat(32))).length, 0);
  assert.equal((await f.offers(bob)).length, 0);
  assert.equal(await count("receipts"), 1);
  assert.deepEqual(await receipted(), [{ id: offer.listing_id, client: bob, market: "0", item_id: "scrap", units: 3, types: ["MATERIAL", "JUNK"], created_at: 1000, sent_at: 1000 }]);
});

test("a receipt keeps the listing's counted units and original time", async () => {
  const f = setup({ maxUnitsPerDay: 2 });
  await f.post(alice, "b1", [sale("scrap", 5)], "friends");
  f.clock.now += HOUR;
  await f.offers(bob, "", 30, "friends");
  const [receipt] = await receipted();
  assert.deepEqual([receipt.market, receipt.units, receipt.created_at, receipt.sent_at], ["friends", 0, 1000, 1000 + HOUR]);
});

test("one receipt row holds all the listings a visit sent", async () => {
  const f = setup();
  await f.post(alice, "b1", Array.from({ length: 8 }, (_, index) => sale(`item${index}`, 2, ["FOOD"])));
  rowsWritten = 0;
  const offers = await f.offers(bob, "", 8);
  assert.equal(offers.length, 8);
  assert.equal(await count("receipts"), 1);
  assert.deepEqual((await receipted()).map((row) => row.id).sort(), offers.map((offer) => offer.listing_id).sort());
  // One receipt, one storage update and one deletion for each listing.
  assert.equal(rowsWritten, 10);
});

test("a report can name some of a visit's listings, and the rest stay reportable", async () => {
  const f = setup();
  await f.post(alice, "b1", ["a", "b", "c", "d"].map((id) => sale(id, 4, ["FOOD"])));
  const ids = Object.fromEntries((await f.offers(bob)).map((offer) => [offer.item_id, offer.listing_id]));
  await f.report(bob, [ids.a, ids.b]);
  near((await f.totals("a")).net, 1, 0.0001);
  assert.equal((await f.totals("c")).net, -4);
  assert.deepEqual((await receipted()).map((row) => row.item_id).sort(), ["c", "d"]);
  await f.report(bob, [ids.a, ids.c]);
  near((await f.totals("a")).net, 1, 0.0001);
  assert.equal((await f.totals("a")).volume, 8);
  near((await f.totals("c")).net, 1, 0.0001);
  assert.deepEqual((await receipted()).map((row) => row.item_id), ["d"]);
  f.clock.now += 13 * DAY;
  await f.report(bob, [ids.d]);
  near((await f.totals("d")).net, 1, 0.0001);
  assert.equal(await count("receipts"), 0);
});

test("a report can name listings from several visits", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("a", 4), sale("b", 4)]);
  const first = await f.offers(bob);
  f.clock.now += 1;
  await f.post(alice, "b2", [sale("c", 4), sale("d", 4)]);
  const second = await f.offers(bob);
  assert.equal(await count("receipts"), 2);
  const id = (offers, item) => offers.find((offer) => offer.item_id === item).listing_id;
  await f.report(bob, [id(first, "a"), id(second, "c"), "unknown", "mint:0123456789abcdef:0"]);
  near((await f.totals("a")).net, 1, 0.001);
  near((await f.totals("c")).net, 1, 0.001);
  assert.equal((await f.totals("b")).net, -4);
  assert.equal(await count("receipts"), 2);
  await f.report(bob, [id(first, "b"), id(second, "d"), id(first, "a")]);
  near((await f.totals("a")).net, 1, 0.001);
  near((await f.totals("b")).net, 1, 0.001);
  near((await f.totals("d")).net, 1, 0.001);
  assert.equal(await count("receipts"), 0);
});

test("another client's report leaves a visit's receipt as it was", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("a", 4), sale("b", 4)]);
  const offers = await f.offers(bob);
  rowsWritten = 0;
  await f.report("c".repeat(32), offers.map((offer) => offer.listing_id));
  assert.equal(rowsWritten, 0);
  assert.equal((await receipted()).length, 2);
});

test("a receipt expires the longest listing lifetime after it was sent", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("a", 4), sale("b", 4)]);
  const offers = await f.offers(bob);
  f.clock.now += 14 * DAY - 1;
  await f.report(bob, [offers[0].listing_id]);
  near((await f.totals(offers[0].item_id)).net, 1, 0.0001);
  assert.equal((await receipted()).length, 1);
  f.clock.now += 2;
  await f.report(bob, [offers[1].listing_id]);
  assert.equal((await f.totals(offers[1].item_id)).net, -4);
  assert.equal((await receipted()).length, 1);
  await f.market.prune();
  assert.equal(await count("receipts"), 0);
});

test("two buyers at once never get the same listing", async () => {
  const f = setup();
  await f.post(alice, "b1", Array.from({ length: 10 }, (_, index) => sale(`item${index}`)));
  const carol = "c".repeat(32);
  const [first, second] = await Promise.all([f.offers(bob, "", 10), f.offers(carol, "", 10)]);
  const ids = [...first, ...second].map((offer) => offer.listing_id);
  assert.equal(ids.length, 10);
  assert.equal(new Set(ids).size, 10);
});

test("old listings stop being offered before cleanup runs", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("scrap")]);
  f.clock.now += 15 * DAY;
  assert.equal((await f.offers(bob)).length, 0);
});

test("cleanup removes old records and keeps recent ones", async () => {
  const f = setup();
  await f.post(alice, "old", [sale("scrap")]);
  f.clock.now += 15 * DAY;
  await f.post(alice, "new", [sale("bread")]);
  await f.market.prune();
  assert.equal(await count("listings"), 1);
  assert.equal(await count("trades"), 1);
  assert.equal(await count("category_trades"), 1);
  assert.equal(await count("batches"), 1);
});

test("batches are remembered for a week", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("scrap")]);
  f.clock.now += 6 * DAY;
  await f.market.prune();
  assert.equal(await count("batches"), 1);
  f.clock.now += 2 * DAY;
  await f.market.prune();
  assert.equal(await count("batches"), 0);
});

test("past the batch limit, cleanup forgets the oldest", async () => {
  const f = setup({ maxBatches: 2 });
  for (const id of ["b1", "b2", "b3"]) {
    f.clock.now += 1;
    await f.post(alice, id, [sale("scrap")]);
  }
  await f.market.prune();
  const { results } = await db.prepare("SELECT batch_id FROM batches ORDER BY batch_id").all();
  assert.deepEqual(results.map((row) => row.batch_id), ["b2", "b3"]);
});

test("a category request looks at a fixed number of listings, however many the market holds", async () => {
  const f = setup({ mintOffers: true, minRealShare: 0.25, maxRealShare: 0.5, maxSellerShare: 0.5 });
  const costs = [];
  for (let batch = 0; batch < 100; batch++) {
    await f.post(alice, `b${batch}`, Array.from({ length: 20 }, (_, index) => sale(`tool${index}`, 1, ["TOOL"])));
    if (batch === 29 || batch === 99) {
      rowsRead = 0;
      const offers = await f.offers(bob, "FOOD", 8);
      assert.equal(offers.length, 0);
      costs.push(rowsRead);
    }
  }
  // At most 500 listings looked at, out of 600 and then 2000, plus two rows for pricing.
  assert.ok(costs.every((cost) => cost <= 502), `read ${costs}`);
  // A rare listing beyond the rows looked at isn't found, and minted offers would fill its slot.
  await f.post(alice, "rare", [sale("bread", 1, ["FOOD"])]);
  let found = 0;
  for (let visit = 0; visit < 40; visit++) {
    const offers = await f.offers(bob, "FOOD", 8);
    assert.ok(offers.every((offer) => offer.item_id === "bread" || offer.minted));
    found += offers.some((offer) => offer.item_id === "bread") ? 1 : 0;
    if (found > 0) {
      break;
    }
  }
  assert.equal(found, 1, "a walk from a random point should reach it within 40 tries");
});

test("a category matches whole names only", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 1, ["FRESH_FOOD"]), sale("cheese", 1, ["FOOD"])]);
  assert.deepEqual((await f.offers(bob, "FOOD")).map((offer) => offer.item_id), ["cheese"]);
});

test("markets don't share listings or prices", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 50, ["FOOD"])], "friends");
  assert.equal((await f.offers(bob, "FOOD")).length, 0);
  assert.equal((await f.offers(bob, "", 30, "other")).length, 0);
  assert.equal((await f.price("bread")).buy_multiplier, 1);
  assert.ok((await f.price("bread", "friends")).buy_multiplier < 1);
  assert.equal((await f.offers(bob, "FOOD", 30, "friends")).length, 1);
});

test("the same batch ID in two markets counts in each", async () => {
  const f = setup();
  assert.equal((await f.post(alice, "b1", [sale("bread")], "one")).listed, 1);
  assert.equal((await f.post(alice, "b1", [sale("bread")], "two")).listed, 1);
  assert.equal(await count("listings"), 2);
});

test("a purchase counts in the market it was sent from, whatever market the report comes from", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 1, ["FOOD"])], "friends");
  const id = (await f.offers(bob, "", 30, "friends"))[0].listing_id;
  await f.offers(bob, "", 30, "0", [id]);
  near((await f.totals("bread", "friends")).net, 0.25, 0.0001);
  near((await f.categoryTotals("FOOD", "friends")).net, 0.25, 0.0001);
  assert.equal(await f.totals("bread", "0"), undefined);
  assert.equal(await count("receipts"), 0);
});

test("market codes ignore case and surrounding spaces", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread")], "Friends ");
  assert.equal((await f.offers(bob, "", 30, "friends")).length, 1);
});

test("no market code means market 0", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread")]);
  assert.equal((await f.offers(bob, "", 30, "0")).length, 1);
});

test("an offers request stays cheap in a large market", async () => {
  const f = setup({ mintOffers: true, minRealShare: 0.25, maxRealShare: 0.5, maxSellerShare: 0.5 });
  for (let batch = 0; batch < 100; batch++) {
    const items = Array.from({ length: 20 }, (_, index) => sale(`item${index}`, 1, ["FOOD", "TOOL"]));
    await f.post(alice, `b${batch}`, items);
  }
  for (const category of ["", "FOOD"]) {
    for (let visit = 0; visit < 5; visit++) {
      f.clock.now += 1;
      rowsRead = 0;
      const offers = await f.offers(bob, category, 8);
      assert.equal(offers.length, 8);
      assert.ok(rowsRead < 300, `${category || "no category"}: read ${rowsRead} rows out of 2000 listings`);
    }
  }
});

test("reporting purchases and selling stay cheap in a large market", async () => {
  const f = setup({ mintOffers: true, minRealShare: 0.5, maxRealShare: 0.5 });
  for (let batch = 0; batch < 100; batch++) {
    const items = Array.from({ length: 20 }, (_, index) => sale(`item${index}`, 1, ["FOOD", "TOOL"]));
    await f.post(alice, `b${batch}`, items);
  }
  // Unreported receipts of this client's and others' from earlier visits.
  for (let visit = 0; visit < 20; visit++) {
    f.clock.now += 1;
    await f.offers(visit % 2 ? bob : "c".repeat(32), "FOOD", 8);
  }
  f.clock.now += 1;
  const offers = await f.offers(bob, "FOOD", 8);
  f.clock.now += 1;
  const mint = offers.find((offer) => offer.minted);
  const real = offers.filter((offer) => !offer.minted);
  assert.ok(mint && real.length > 0);
  // A report reads each of the client's own receipts and its listings, here 11 visits of 4, but nothing of the market's.
  rowsRead = 0;
  await f.report(bob, [...real.map((offer) => offer.listing_id), mint.listing_id]);
  assert.ok(rowsRead < 300, `a report read ${rowsRead} rows out of 4000 listings`);
  rowsRead = 0;
  f.clock.now += 1;
  await f.offers(bob, "FOOD", 8, undefined, [...real.map((offer) => offer.listing_id), mint.listing_id]);
  assert.ok(rowsRead < 400, `offers with a report read ${rowsRead} rows`);
  rowsRead = 0;
  f.clock.now += 1;
  await f.post(bob, "more", Array.from({ length: 8 }, (_, index) => sale(`item${index}`, 1, ["FOOD", "TOOL"])));
  assert.ok(rowsRead < 150, `a sale read ${rowsRead} rows`);
});

// A visit is a sale, then an offers request that reports what the last visit bought.
test("a visit's writes match the free plan estimate", async () => {
  const f = setup();
  await f.post("c".repeat(32), "stock", Array.from({ length: 16 }, (_, index) => sale(`stock${index}`, 1, ["FOOD"])));
  const last = await f.offers(bob, "FOOD", 8);
  f.clock.now += 1;
  rowsWritten = 0;
  rowsRead = 0;
  await f.post(alice, "b1", Array.from({ length: 8 }, (_, index) => sale(`item${index}`, 1, ["FOOD"])));
  const offers = await f.offers(bob, "FOOD", 8, undefined, last.slice(0, 4).map((offer) => offer.listing_id));
  assert.equal(offers.length, 8);
  assert.ok(rowsWritten < 50, `wrote ${rowsWritten} rows`);
  assert.ok(rowsRead < 400, `read ${rowsRead} rows`);
});

test("a minted offer copies a listing's value and types, even one the same visit takes, and isn't stored", async () => {
  const f = setup({ mintOffers: true });
  const response = await f.post(alice, "b1", [{ ...sale("bread", 3, ["FOOD", "TREAT"]), value: 250 }]);
  assert.equal(response.listed, 1);
  assert.equal(await count("listings"), 1);
  const offers = await f.offers(bob, "FOOD");
  assert.equal(await count("listings"), 0);
  assert.equal(await count("receipts"), 1);
  const mint = offers.find((offer) => offer.minted);
  assert.equal(offers.length, 2);
  assert.equal(mint.value, 250);
  assert.deepEqual(mint.types.sort(), ["FOOD", "TREAT"]);
  assert.equal(mint.item_id, "");
  assert.equal(mint.payload, "");
});

test("minted offers only fill the slots real listings leave", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", Array.from({ length: 5 }, (_, index) => ({ ...sale(`item${index}`, 1, ["FOOD"]), value: 100 })));
  const offers = await f.offers(bob, "FOOD", 7);
  assert.equal(offers.filter((offer) => !offer.minted).length, 5);
  assert.equal(offers.filter((offer) => offer.minted).length, 2);
  // Sent listings have left the market, so there's nothing left to copy, and the buyer's game mints instead.
  assert.equal((await f.offers(bob, "", 3)).length, 0);
});

test("real listings fill all the slots before minted ones", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", Array.from({ length: 5 }, (_, index) => ({ ...sale(`item${index}`), value: 100 })));
  const offers = await f.offers(bob, "", 5);
  assert.equal(offers.filter((offer) => offer.minted).length, 0);
});

test("a sale that can't be listed mints nothing", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", [{ item_id: "scrap", count: 1, types: [1], payload: "x", value: 100 }]);
  assert.equal(await count("listings"), 0);
});

test("sellers aren't offered their own minted items", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", [{ ...sale("bread"), value: 100 }]);
  assert.equal((await f.offers(alice)).length, 0);
});

test("each visit mints from the market again, with offer IDs of its own", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", [{ ...sale("bread"), value: 100 }]);
  const first = (await f.offers(bob)).find((offer) => offer.minted);
  await f.post(alice, "b2", [{ ...sale("bread"), value: 100 }]);
  const second = (await f.offers("c".repeat(32), "", 30, undefined, [first.listing_id])).find((offer) => offer.minted);
  assert.ok(second);
  assert.notEqual(second.listing_id, first.listing_id);
});

test("a minted offer is priced by its category", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", [{ ...sale("bread", 30, ["FOOD"]), value: 100 }]);
  const mint = (await f.offers(bob, "FOOD")).find((offer) => offer.minted);
  near(mint.buy_multiplier, Math.exp(-30 / 300), 0.0001);
});

test("buying a minted offer doesn't move prices", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", [{ ...sale("bread", 1, ["FOOD"]), value: 100 }]);
  const mint = (await f.offers(bob, "FOOD")).find((offer) => offer.minted);
  rowsWritten = 0;
  await f.report(bob, [mint.listing_id]);
  assert.equal(rowsWritten, 0);
  assert.equal(await count("trades"), 1);
  near((await f.totals("bread")).net, -1, 0.0001);
  assert.equal((await f.categoryTotals("FOOD")).volume, 1);
  assert.equal(await count("receipts"), 1);
});

test("minted offers stay in their market", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", [{ ...sale("bread"), value: 100 }], "friends");
  assert.equal((await f.offers(bob)).length, 0);
  assert.equal((await f.offers(bob, "", 30, "friends")).length, 2);
});

test("old minted offers are cleaned up", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", [{ ...sale("bread"), value: 100 }]);
  f.clock.now += 15 * DAY;
  await f.market.prune();
  assert.equal(await count("listings"), 0);
});

test("a visit's writes with minting", async () => {
  const f = setup({ mintOffers: true, minRealShare: 0.25, maxRealShare: 0.5, maxSellerShare: 1 });
  await f.post("c".repeat(32), "stock", Array.from({ length: 8 }, (_, index) => ({ ...sale(`item${index}`, 1, ["FOOD"]), value: 100 })));
  const last = await f.offers(bob, "FOOD", 8);
  const real = last.filter((offer) => !offer.minted);
  const minted = last.filter((offer) => offer.minted);
  assert.ok(minted.length >= 4);
  const bought = [...real.slice(0, 2), ...minted.slice(0, 2)].map((offer) => offer.listing_id);
  f.clock.now += 1;
  rowsWritten = 0;
  await f.post(alice, "b1", Array.from({ length: 8 }, (_, index) => ({ ...sale(`item${index}`, 1, ["FOOD"]), value: 100 })));
  assert.equal((await f.offers(bob, "FOOD", 8, undefined, bought)).length, 8);
  assert.ok(rowsWritten < 40, `wrote ${rowsWritten} rows`);
});

test("offers carry the category modifiers the caller asks for", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 30, ["FOOD"])]);
  const { categories } = await f.market.postOffers({ client: bob, category: "TOOL", limit: 8, categories: ["FOOD", "TOOL"] });
  near(categories.FOOD, Math.exp(-30 / 300), 0.0001);
  assert.equal(categories.TOOL, 1);
  assert.deepEqual(Object.keys(categories).sort(), ["FOOD", "TOOL"]);
});

test("category modifiers come back even with nothing to offer, and fade", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 30, ["FOOD"])], "friends");
  f.clock.now += 2 * HOUR;
  const empty = await f.market.postOffers({ market: "friends", client: alice, category: "FOOD", limit: 8, categories: ["FOOD"] });
  assert.equal(empty.offers.length, 0);
  near(empty.categories.FOOD, Math.exp(-15 / 300), 0.0001);
  assert.equal((await f.market.postOffers({ market: "other", client: alice, category: "FOOD", limit: 8, categories: ["FOOD"] })).categories.FOOD, 1);
});

test("asking for category modifiers writes nothing", async () => {
  const f = setup();
  rowsWritten = 0;
  await f.market.postOffers({ client: bob, category: "", limit: 8, categories: ["FOOD", "TOOL"], bought: [] });
  assert.equal(rowsWritten, 0);
});

test("unusable category names are ignored and the list is capped", async () => {
  const f = setup();
  const many = ["FOOD", "MEDICAL", "MATERIAL", "HOUSEHOLD_GOOD", "SUBSTANCE", "WEAPON", "WATER", "ARMOR", "TOOL", "MODULE", "AMMUNITION", "RARE", "JUNK", "TYPE1"];
  assert.equal(Object.keys((await f.market.postOffers({ client: bob, categories: many })).categories).length, 11);
  assert.deepEqual((await f.market.postOffers({ client: bob, categories: ["not safe!", "", "FOOD", "FOOD"] })).categories, { FOOD: 1 });
});

test("with a drift secret, a quiet market's categories drift, the same for everyone in it", async () => {
  const f = setup({ driftSecret: "test-secret" });
  const ask = (client, code) => f.market.postOffers({ market: code, client, limit: 8, categories: ["FOOD", "TOOL"] });
  const { categories } = await ask(bob);
  for (const rate of Object.values(categories)) {
    assert.ok(rate !== 1 && rate >= Math.exp(-0.22) - 0.0001 && rate <= Math.exp(0.22) + 0.0001, `rate ${rate}`);
  }
  assert.deepEqual((await ask(alice)).categories, categories);
  assert.notDeepEqual((await ask(bob, "friends")).categories, categories);
  f.clock.now += 3 * HOUR;
  assert.notDeepEqual((await ask(bob)).categories, categories);
});

test("drift adds to trade pressure, and an item moves with its major categories only", async () => {
  const f = setup({ driftSecret: "test-secret" });
  const { categories } = await f.market.postOffers({ client: bob, limit: 8, categories: ["FOOD"] });
  await f.post(alice, "b1", [sale("bread", 3, ["FOOD", "FRESH_FOOD"])]);
  const { offers } = await f.market.postOffers({ client: bob, category: "FOOD", limit: 8, categories: [] });
  // Trade pressure averages over both categories, but the drift is FOOD's alone, not halved by FRESH_FOOD.
  near(offers[0].buy_multiplier, Math.exp(-3 / 20 - 3 / 300) * categories.FOOD, 0.0001);
});

test("a sale's prices include the drift", async () => {
  const quiet = setup({ driftSecret: "test-secret" });
  const { categories } = await quiet.market.postOffers({ client: bob, limit: 8, categories: ["TOOL"] });
  const response = await quiet.post(alice, "b1", [sale("wrench", 1, ["TOOL"])]);
  near(response.prices[0].buy_multiplier, Math.exp(-1 / 20 - 1 / 300) * categories.TOOL, 0.0001);
});

test("an offer's own category still prices it when also asked about", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread", 3, ["FOOD"])]);
  const { offers, categories } = await f.market.postOffers({ client: bob, category: "FOOD", limit: 8, categories: ["FOOD"] });
  near(offers[0].buy_multiplier, Math.exp(-3 / 20 - 3 / 300), 0.0001);
  near(categories.FOOD, Math.exp(-3 / 300), 0.0001);
});

test("stored bytes follow listings through sales, offers and cleanup", async () => {
  const f = setup();
  const item = (id) => ({ ...sale(id), payload: "酒".repeat(10) });
  await f.post(alice, "b1", [item("a"), item("b")]);
  assert.equal(await storedBytes(), 60);
  await f.post(alice, "b1", [item("a"), item("b")]);
  assert.equal(await storedBytes(), 60);
  const [first] = await f.offers(bob, "", 1);
  assert.equal(await storedBytes(), 30);
  await f.report(bob, [first.listing_id]);
  assert.equal(await storedBytes(), 30);
  await db.prepare("UPDATE storage SET listing_bytes = 999").run();
  await f.market.prune();
  assert.equal(await storedBytes(), 30);
  f.clock.now += 15 * DAY;
  await f.market.prune();
  assert.equal(await storedBytes(), 0);
});

test("a full market still pays for a sale but lists nothing", async () => {
  const f = setup({ mintOffers: true, maxListingBytes: 20 });
  const item = { ...sale("bread", 3, ["FOOD"]), payload: "x".repeat(21), value: 100 };
  const response = await f.post(alice, "b1", [item]);
  assert.equal(response.listed, 0);
  near(response.prices[0].buy_multiplier, Math.exp(-3 / 20 - 3 / 300), 0.0001);
  assert.equal(await count("listings"), 0);
  assert.equal(await storedBytes(), 0);
  assert.equal((await f.totals("bread")).volume, 3);
  assert.deepEqual(await f.post(alice, "b1", [item]), response);
});

test("a market with room is unaffected, and makes room when listings go", async () => {
  const f = setup({ maxListingBytes: 20 });
  const item = (id) => ({ ...sale(id), payload: "x".repeat(15) });
  assert.equal((await f.post(alice, "b1", [item("a")])).listed, 1);
  assert.equal((await f.post(alice, "b2", [item("b")])).listed, 0);
  await f.offers(bob);
  assert.equal((await f.post(alice, "b3", [item("c")])).listed, 1);
});

test("offers keep working when the market is full", async () => {
  const f = setup({ maxListingBytes: 20 });
  await f.post(alice, "b1", [{ ...sale("a"), payload: "x".repeat(20) }]);
  await f.post(alice, "b2", [{ ...sale("b"), payload: "y".repeat(20) }]);
  assert.equal((await f.offers(bob)).length, 1);
});

test("real offers carry their value and categories", async () => {
  const f = setup();
  await f.post(alice, "b1", [{ ...sale("gun", 1, ["WEAPON", "RARE"]), value: 500 }]);
  const [offer] = await f.offers(bob);
  assert.equal(offer.value, 500);
  assert.deepEqual(offer.types.sort(), ["RARE", "WEAPON"]);
  assert.equal(offer.minted, undefined);
});

test("a bought listing counts for its own item, whatever the buyer's game made of it", async () => {
  const f = setup();
  await f.post(alice, "b1", [{ ...sale("modded_gun", 4, ["WEAPON"]), value: 500 }]);
  const [offer] = await f.offers(bob);
  const report = () => f.market.postOffers({ market: "nowhere", client: bob, bought: [offer.listing_id], minted_items: { [offer.listing_id]: "pistol" } });
  await report();
  await report();
  assert.equal(await count("listings"), 0);
  assert.equal(await f.totals("pistol"), undefined);
  near((await f.totals("modded_gun")).net, 1, 0.0001);
  assert.equal((await f.totals("modded_gun")).volume, 8);
  near((await f.categoryTotals("WEAPON")).net, 1, 0.0001);
  assert.equal(await storedBytes(), 0);
});

test("a player's items stop reaching the market at the daily limit", async () => {
  const f = setup({ mintOffers: true, maxListingsPerDay: 3 });
  const items = Array.from({ length: 5 }, (_, index) => sale(`item${index}`));
  const response = await f.post(alice, "b1", items);
  assert.equal(response.listed, 3);
  assert.equal(response.prices.length, 5);
  assert.equal(await count("listings"), 3);
  assert.equal(await count("trades"), 3);
  assert.equal((await f.post(alice, "b2", [sale("late")])).listed, 0);
  assert.equal(await f.totals("late"), undefined);
  assert.equal((await f.post(bob, "b3", [sale("other")])).listed, 1);
  f.clock.now += DAY;
  assert.equal((await f.post(alice, "b4", [sale("late")])).listed, 1);
});

test("past the daily units, a sale is listed but doesn't move prices", async () => {
  const f = setup({ maxUnitsPerDay: 10 });
  assert.equal((await f.post(alice, "b1", [sale("a", 8), sale("b", 8), sale("c", 2)])).listed, 3);
  assert.equal((await f.totals("a")).volume, 8);
  assert.equal(await f.totals("b"), undefined);
  assert.equal((await f.totals("c")).volume, 2);
  const uncounted = (await f.offers(bob)).find((offer) => offer.item_id === "b");
  await f.report(bob, [uncounted.listing_id]);
  assert.equal((await f.totals("b")).net, 0);
  assert.equal((await f.totals("b")).volume, 0);
});

function nearAll(got, want) {
  assert.deepEqual(Object.keys(got).sort(), Object.keys(want).sort());
  for (const [item, days] of Object.entries(want)) {
    near(got[item], days, 0.0001);
  }
}

test("a listing lasts less the more live listings its seller holds", async () => {
  const { market } = setup();
  assert.equal(market["lifetime"](0, 100), 14);
  assert.equal(market["lifetime"](50, 100), 11.25);
  assert.equal(market["lifetime"](100, 100), 3);
  assert.equal(market["lifetime"](150, 100), 3);
  assert.equal(setup({ listingDays: 10, minListingDays: 2 }).market["lifetime"](50, 100), 8);
  assert.equal(market["lifetime"](5, Infinity), 14);
});

test("each listing in a batch counts the ones before it, and nothing is refused past the live number", async () => {
  const f = setup({ maxLiveListings: 4 });
  assert.equal((await f.post(alice, "b1", ["a", "b", "c", "d", "e", "f"].map((id) => sale(id)))).listed, 6);
  nearAll(await expiries(), { a: 14, b: 3 + 11 * (15 / 16), c: 11.25, d: 3 + 11 * (7 / 16), e: 3, f: 3 });
  assert.equal((await db.prepare("SELECT live FROM seller_quota WHERE key = ?").bind(alice).first()).live, 6);
});

test("a sale counts on from the seller's earlier sales, including listings sent since, until cleanup counts again", async () => {
  const f = setup({ maxLiveListings: 4 });
  const live = async () => (await db.prepare("SELECT day, listings, live FROM seller_quota WHERE key = ? ORDER BY day").bind(alice).all()).results;
  await f.post(alice, "b1", [sale("a"), sale("b")]);
  assert.equal((await f.offers(bob)).length, 2);
  // Both were sent, but only the cleanup notices.
  await f.post(alice, "b2", [sale("c")]);
  nearAll(await expiries(), { c: 11.25 });
  await f.market.prune();
  assert.deepEqual(await live(), [{ day: 0, listings: 3, live: 1 }]);
  await f.post(alice, "b3", [sale("d")]);
  nearAll(await expiries(), { c: 11.25, d: 3 + 11 * (15 / 16) });
  // A new day before its cleanup carries on from yesterday's count.
  f.clock.now += DAY;
  await f.post(alice, "b4", [sale("e")]);
  near((await expiries()).e, 11.25, 0.0001);
  assert.deepEqual(await live(), [{ day: 0, listings: 4, live: 2 }, { day: 1, listings: 1, live: 3 }]);
  await f.offers(bob, "", 1);
  await f.market.prune();
  assert.deepEqual(await live(), [{ day: 1, listings: 1, live: 2 }]);
});

test("cleanup counts a key with nothing left as zero, and keeps its daily usage", async () => {
  const f = setup({ maxLiveListings: 4, maxListingsPerDay: 3 });
  await f.post(alice, "b1", [sale("a"), sale("b")]);
  await f.offers(bob);
  await f.market.prune();
  assert.deepEqual(await db.prepare("SELECT listings, live FROM seller_quota WHERE key = ?").bind(alice).first(), { listings: 2, live: 0 });
  assert.equal((await f.post(alice, "b2", [sale("c"), sale("d")])).listed, 1);
  nearAll(await expiries(), { c: 14 });
});

test("an address's listings shorten the lifetime of each client ID's, and cleanup counts both", async () => {
  const f = setup({ maxLiveListings: 4, sourceQuotaFactor: 2 });
  const address = "f".repeat(32);
  const carol = "c".repeat(32);
  await f.post(alice, "b1", [sale("a1"), sale("a2"), sale("a3")], undefined, address);
  await f.post(bob, "b1", [sale("b1")], undefined, address);
  await f.post(carol, "b1", [sale("c1")], undefined, "e".repeat(32));
  nearAll(await expiries(), { a1: 14, a2: 3 + 11 * (15 / 16), a3: 11.25, b1: 3 + 11 * (1 - 9 / 64), c1: 14 });
  await db.prepare("UPDATE seller_quota SET live = 99").run();
  await f.market.prune();
  const { results } = await db.prepare("SELECT key, live FROM seller_quota ORDER BY key").all();
  assert.deepEqual(results, [
    { key: alice, live: 3 },
    { key: bob, live: 1 },
    { key: carol, live: 1 },
    { key: "e".repeat(32), live: 1 },
    { key: address, live: 4 },
  ]);
  // A fifth listing from the address is its own fifth of eight.
  await f.post("d".repeat(32), "b1", [sale("d1")], undefined, address);
  near((await expiries()).d1, 3 + 11 * (1 - 16 / 64), 0.0001);
});

test("a listing past its lifetime isn't offered or copied, and cleanup removes it", async () => {
  const f = setup({ mintOffers: true, maxLiveListings: 1 });
  await f.post(alice, "b1", [{ ...sale("long"), value: 100 }, { ...sale("short"), value: 999 }]);
  nearAll(await expiries(), { long: 14, short: 3 });
  f.clock.now += 3 * DAY;
  const offers = await f.offers(bob);
  assert.deepEqual(offers.filter((offer) => !offer.minted).map((offer) => offer.item_id), ["long"]);
  assert.ok(offers.filter((offer) => offer.minted).every((offer) => offer.value === 100));
  assert.equal(await count("listings"), 1);
  await f.market.prune();
  assert.equal(await count("listings"), 0);
  assert.equal(await storedBytes(), 0);
});

test("a repeated batch counts its listings toward the live number once", async () => {
  const f = setup({ maxLiveListings: 4 });
  await f.post(alice, "b1", [sale("a")]);
  await f.post(alice, "b1", [sale("a")]);
  await f.post(alice, "b2", [sale("b")]);
  nearAll(await expiries(), { a: 14, b: 3 + 11 * (15 / 16) });
});

test("a visit's real offers stay between a quarter and half, with no seller taking over", async () => {
  const counts = new Set();
  for (let visit = 0; visit < 8; visit++) {
    await db.batch(["listings", "receipts", "seller_quota", "batches"].map((table) => db.prepare(`DELETE FROM ${table}`)));
    const f = setup({ mintOffers: true, minRealShare: 0.25, maxRealShare: 0.5, maxSellerShare: 0.5 });
    for (const digit of ["1", "2", "3", "4"]) {
      await f.post(digit.repeat(32), "b1", Array.from({ length: 5 }, (_, index) => sale(`item${index}`)));
    }
    const seller = await sellerOf();
    const offers = await f.offers(bob, "", 8);
    const real = offers.filter((offer) => !offer.minted);
    assert.equal(offers.length, 8);
    assert.ok(real.length >= 2 && real.length <= 4, `${real.length} real offers`);
    const bySeller = Object.groupBy(real, seller);
    assert.ok(Object.values(bySeller).every((group) => group.length <= 2));
    counts.add(real.length);
  }
  assert.ok(counts.size > 1, "the share should vary between visits");
});

test("real listings fill the slots when minted offers run out, within the seller limit", async () => {
  const f = setup({ minRealShare: 0.25, maxRealShare: 0.25, maxSellerShare: 0.5 });
  for (const digit of ["1", "2", "3", "4", "5", "6", "7", "8"]) {
    await f.post(digit.repeat(32), "b1", [sale("a"), sale("b")]);
  }
  const seller = await sellerOf();
  const offers = await f.offers(bob, "", 8);
  assert.equal(offers.length, 8);
  assert.ok(offers.every((offer) => !offer.minted));
  const bySeller = Object.groupBy(offers, seller);
  assert.ok(Object.values(bySeller).every((group) => group[0] && seller(group[0])));
  assert.ok(Object.values(bySeller).every((group) => group.length === 1));
});

test("allow self offers everything, for testing alone", async () => {
  const f = setup({ allowSelf: true, minRealShare: 0.25, maxRealShare: 0.5, maxSellerShare: 0.5 });
  await f.post(alice, "b1", Array.from({ length: 8 }, (_, index) => sale(`item${index}`)));
  assert.equal((await f.offers(alice, "", 8)).length, 8);
});

test("new client IDs from one address share that address's limit", async () => {
  const f = setup({ maxListingsPerDay: 2, sourceQuotaFactor: 2 });
  const items = (id) => [sale(`${id}1`), sale(`${id}2`), sale(`${id}3`)];
  assert.equal((await f.post(alice, "b1", items("a"), undefined, "f".repeat(32))).listed, 2);
  assert.equal((await f.post(bob, "b2", items("b"), undefined, "f".repeat(32))).listed, 2);
  assert.equal((await f.post("c".repeat(32), "b3", items("c"), undefined, "f".repeat(32))).listed, 0);
  assert.equal((await f.post("c".repeat(32), "b4", items("c"), undefined, "e".repeat(32))).listed, 2);
});

test("changing market code doesn't reset the limit", async () => {
  const f = setup({ maxListingsPerDay: 1 });
  assert.equal((await f.post(alice, "b1", [sale("a")], "one")).listed, 1);
  assert.equal((await f.post(alice, "b1", [sale("a")], "two")).listed, 0);
});

test("a repeated batch uses the quota once", async () => {
  const f = setup({ maxListingsPerDay: 1 });
  const first = await f.post(alice, "b1", [sale("a")]);
  assert.deepEqual(await f.post(alice, "b1", [sale("a")]), first);
  assert.equal((await db.prepare("SELECT listings FROM seller_quota").first()).listings, 1);
});

test("cleanup forgets old quota days, keeping only today's live count", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("a")]);
  await f.market.prune();
  assert.equal(await count("seller_quota"), 1);
  f.clock.now += DAY;
  await f.market.prune();
  assert.deepEqual((await db.prepare("SELECT * FROM seller_quota").all()).results, [{ key: alice, day: 1, listings: 0, units: 0, live: 1 }]);
  f.clock.now += 14 * DAY;
  await f.market.prune();
  assert.equal(await count("seller_quota"), 0);
});

// A sale reads in its first batch and writes in its second. This holds every sale's read until all
// of them have read, so they all decide on the same usage: the worst case for limits checked at the read.
function readTogether(sales) {
  const waiting = [];
  let reads = 0;
  return {
    prepare: (sql) => db.prepare(sql),
    batch: async (statements) => {
      const results = await db.batch(statements);
      if (reads < sales) {
        reads++;
        await new Promise((resolve) => {
          waiting.push(resolve);
          if (waiting.length === sales) {
            waiting.forEach((go) => go());
          }
        });
      }
      return results;
    },
  };
}

// Posts sales at once with readTogether, then checks that each stored answer is the one returned.
async function race(change, now, posts) {
  const f = setup(change, readTogether(posts.length));
  f.clock.now = now;
  const responses = await Promise.all(posts.map(([client, batchId, items, source]) => f.post(client, batchId, items, undefined, source)));
  const again = setup(change);
  again.clock.now = now;
  for (const [index, [client, batchId, items, source]] of posts.entries()) {
    assert.deepEqual(await again.post(client, batchId, items, undefined, source), responses[index]);
  }
  return responses;
}

const listedIn = (responses) => responses.reduce((total, response) => total + response.listed, 0);

test("sales racing each other stay within the daily limits", async () => {
  const limits = { maxListingsPerDay: 3, maxUnitsPerDay: 10 };
  const responses = await race(limits, 1000, ["b1", "b2", "b3"].map((id) => [alice, id, [sale(`${id}x`, 4), sale(`${id}y`, 4)]]));
  const listed = listedIn(responses);
  assert.ok(listed > 0 && listed <= 3, `listed ${listed}`);
  assert.equal(await count("listings"), listed);
  const quota = await db.prepare("SELECT listings, units FROM seller_quota WHERE key = ?").bind(alice).first();
  assert.ok(quota.listings <= 3, `quota says ${quota.listings} listings`);
  assert.ok(quota.units <= 10, `quota says ${quota.units} units`);
  const { volume } = await db.prepare("SELECT COALESCE(SUM(volume), 0) AS volume FROM trades").first();
  assert.ok(volume <= 10, `${volume} units moved prices`);
});

test("sales racing each other keep live counts, lifetimes and stored bytes right", async () => {
  const responses = await race({ maxLiveListings: 4 }, 1000, ["b1", "b2", "b3"].map((id) => [alice, id, [sale(id)]]));
  const listed = listedIn(responses);
  assert.ok(listed > 0);
  assert.equal(await count("listings"), listed);
  const quota = await db.prepare("SELECT listings, live FROM seller_quota WHERE key = ?").bind(alice).first();
  assert.deepEqual(quota, { listings: listed, live: listed });
  // A sale that lost a race reads again, so no two listings were planned from the same count.
  const days = Object.values(await expiries()).sort((a, b) => b - a);
  [14, 3 + 11 * (15 / 16), 11.25].slice(0, listed).forEach((want, index) => near(days[index], want, 0.0001));
  assert.equal(await storedBytes(), listed * '{"saveItems":[]}'.length);
});

test("sales racing each other stay within the storage limit, and still count", async () => {
  const item = (id) => ({ ...sale(id), payload: "x".repeat(20) });
  const carol = "c".repeat(32);
  const responses = await race({ maxListingBytes: 50 }, 1000, [[alice, "b1", [item("a")]], [bob, "b1", [item("b")]], [carol, "b1", [item("c")]]]);
  assert.deepEqual(responses.map((response) => response.listed).sort(), [0, 1, 1]);
  assert.equal(await count("listings"), 2);
  assert.equal(await storedBytes(), 40);
  const f = setup();
  for (const id of ["a", "b", "c"]) {
    assert.equal((await f.totals(id)).volume, 1);
  }
});

test("listings another buyer takes after an offers request has looked are left out of its offers", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("a"), sale("b"), sale("c")]);
  let resume;
  let readDone;
  const hold = new Promise((resolve) => (resume = resolve));
  const read = new Promise((resolve) => (readDone = resolve));
  let reads = 0;
  const pausing = {
    prepare: (sql) => db.prepare(sql),
    batch: async (statements) => {
      const results = await db.batch(statements);
      if (reads++ === 0) {
        readDone();
        await hold;
      }
      return results;
    },
  };
  const slow = setup({}, pausing);
  // Its first batch is the read of candidates, and the send waits until another buyer has taken two.
  const offering = slow.offers(bob);
  await read;
  const first = await f.offers("c".repeat(32), "", 2);
  resume();
  const second = await offering;
  assert.equal(first.length, 2);
  assert.equal(second.length, 1);
  assert.equal(new Set([...first, ...second].map((offer) => offer.listing_id)).size, 3);
  assert.equal(await count("listings"), 0);
  assert.deepEqual((await receipted()).map((row) => row.client).sort(), [bob, "c".repeat(32), "c".repeat(32)]);
  assert.equal(await storedBytes(), 0);
});

test("a sale that keeps losing races is still answered, but lists and counts nothing", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("a")]);
  // Another sale by the same seller goes in before each of this one's reads and writes.
  const rival = {
    prepare: (sql) => db.prepare(sql),
    batch: async (statements) => {
      if (statements.length > 1) {
        await db.prepare("UPDATE seller_quota SET listings = listings + 1 WHERE key = ?").bind(alice).run();
      }
      return db.batch(statements);
    },
  };
  const response = await setup({}, rival).post(alice, "b2", [sale("b", 5)]);
  assert.equal(response.listed, 0);
  assert.equal(response.prices.length, 1);
  assert.equal(await count("listings"), 1);
  assert.equal(await f.totals("b"), undefined);
  assert.deepEqual(await f.post(alice, "b2", [sale("b", 5)]), response);
});

test("one seller only gets a share of the real offers, and minted offers fill the rest", async () => {
  const f = setup({ mintOffers: true, maxSellerShare: 0.25 });
  await f.post(alice, "b1", Array.from({ length: 20 }, (_, index) => sale(`item${index}`)));
  const offers = await f.offers(bob, "", 8);
  assert.equal(offers.filter((offer) => !offer.minted).length, 2);
  assert.equal(offers.filter((offer) => offer.minted).length, 6);
});

test("the seller share follows the address, not the client ID", async () => {
  const f = setup({ maxSellerShare: 0.25 });
  const carol = "c".repeat(32);
  for (const client of [alice, carol]) {
    await f.post(client, "b1", Array.from({ length: 10 }, (_, index) => sale(`item${index}`)), undefined, "f".repeat(32));
  }
  assert.equal((await f.offers(bob, "", 8)).length, 2);
});

test("several sellers fill the offers together", async () => {
  const f = setup({ maxSellerShare: 0.25 });
  const clients = ["1", "2", "3", "4"].map((digit) => digit.repeat(32));
  for (const client of clients) {
    await f.post(client, "b1", Array.from({ length: 5 }, (_, index) => sale(`item${index}`)));
  }
  assert.equal((await f.offers(bob, "", 8)).length, 8);
  assert.equal((await f.offers(bob, "", 8)).length, 8);
});

test("the seller share also applies within a category", async () => {
  const f = setup({ maxSellerShare: 0.25 });
  await f.post(alice, "b1", Array.from({ length: 10 }, (_, index) => sale(`item${index}`, 1, ["FOOD"])));
  await f.post(bob, "b1", [sale("bread", 1, ["FOOD"])]);
  const offers = await f.offers("c".repeat(32), "FOOD", 8);
  assert.equal(offers.filter((offer) => offer.listing_id.startsWith(alice)).length <= 2, true);
});

test("purging a seller removes their listings everywhere and corrects storage", async () => {
  const f = setup({ mintOffers: true });
  await f.post(alice, "b1", [sale("a", 1, ["FOOD"])], "one");
  await f.post(alice, "b1", [sale("b", 1, ["FOOD"])], "two");
  await f.post(bob, "b1", [sale("c", 1, ["FOOD"])]);
  assert.equal(await f.market.purge(alice), 2);
  assert.equal(await count("listings"), 1);
  assert.equal(await storedBytes(), '{"saveItems":[]}'.length);
  assert.equal((await f.post(alice, "b2", [sale("d")])).listed, 1);
});

test("a ban purges a seller and keeps them out, by client ID or address", async () => {
  const f = setup();
  const address = "f".repeat(32);
  await f.post(alice, "b1", [sale("a")], undefined, address);
  assert.equal(await f.market.ban(address), 1);
  assert.equal(await count("listings"), 0);
  const fresh = await f.post(bob, "b2", [sale("b", 50)], undefined, address);
  assert.equal(fresh.listed, 0);
  assert.equal(fresh.prices.length, 1);
  assert.equal(await f.totals("b"), undefined);
  await f.market.unban(address);
  assert.equal((await f.post(bob, "b3", [sale("b")], undefined, address)).listed, 1);
  await f.market.ban(bob);
  assert.equal((await f.post(bob, "b4", [sale("c")], undefined, "e".repeat(32))).listed, 0);
});

test("a purge or ban leaves receipts, so a purchase already sent still counts", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("a", 4), sale("b", 4)]);
  const [first] = await f.offers(bob, "", 1);
  await f.market.purge(alice);
  await f.market.ban(alice);
  assert.equal(await count("listings"), 0);
  assert.equal(await count("receipts"), 1);
  await f.report(bob, [first.listing_id]);
  near((await f.totals(first.item_id)).net, 1, 0.0001);
});

test("the seller list shows who holds the most listings", async () => {
  const f = setup();
  const payload = JSON.stringify({ seller: "Mallory" });
  await f.post(alice, "b1", [{ ...sale("a"), payload }, { ...sale("b"), payload }], undefined, "f".repeat(32));
  await f.post(bob, "b1", [sale("c")]);
  await f.post(bob, "b1", [sale("d"), sale("e"), sale("f")], "elsewhere");
  const [first, second, third] = await f.market.sellers("0");
  assert.equal(third, undefined);
  assert.equal(first.source, "f".repeat(32));
  assert.equal(first.listings, 2);
  assert.equal(first.listings_today, 2);
  assert.equal(first.units_today, 2);
  assert.deepEqual(first.clients, [alice]);
  assert.deepEqual(first.names, ["Mallory"]);
  assert.equal(second.source, bob);
  assert.deepEqual(second.names, []);
});

function serve(limit = 100, salesLimit = 100, secrets = { IP_SALT: "salt" }) {
  const f = setup();
  const hits = new Map();
  const limiter = {
    limit: async ({ key }) => {
      hits.set(key, (hits.get(key) ?? 0) + 1);
      return { success: hits.get(key) <= limit };
    },
  };
  const salesHits = new Map();
  const salesLimiter = {
    limit: async ({ key }) => {
      salesHits.set(key, (salesHits.get(key) ?? 0) + 1);
      return { success: salesHits.get(key) <= salesLimit };
    },
  };
  return async (path, body, headers = {}) => {
    const init = body === undefined ? { headers } : { method: "POST", body: typeof body === "string" ? body : JSON.stringify(body), headers: { "Content-Type": "application/json", ...headers } };
    const response = await handle(new Request(`https://market.test${path}`, init), f.market, limiter, salesLimiter, secrets);
    return { status: response.status, body: await response.json() };
  };
}

test("a sale then an offer round trip over HTTP", async () => {
  const request = serve();
  const posted = await request("/v1/sales", { client: alice, batch_id: "b1", market: "friends", items: [sale("bread", 1, ["FOOD"])] });
  assert.equal(posted.status, 200);
  const body = { market: "friends", client: bob, category: "FOOD", limit: 8, categories: ["FOOD"], bought: [] };
  const offers = await request("/v1/offers", body);
  assert.equal(offers.status, 200);
  assert.deepEqual(Object.keys(offers.body).sort(), ["categories", "fee", "offers"]);
  const offer = offers.body.offers[0];
  assert.equal(offer.item_id, "bread");
  assert.equal(offer.payload, '{"saveItems":[]}');
  const next = await request("/v1/offers", { ...body, bought: [offer.listing_id] });
  assert.equal(next.status, 200);
  assert.deepEqual(next.body.offers, []);
  near(next.body.categories.FOOD, Math.exp(-1 / 300 + 1 / 300 + 0.25 / 300), 0.0001);
});

test("empty offers is an empty list", async () => {
  const request = serve();
  assert.deepEqual((await request("/v1/offers", { client: bob })).body, { offers: [], categories: {}, fee: 0.1 });
});

test("the old offer routes are gone", async () => {
  const request = serve();
  await request("/v1/sales", { client: alice, batch_id: "b1", items: [sale("bread")] });
  assert.equal((await request(`/v1/offers?client=${bob}`)).status, 404);
  assert.equal((await request("/v1/offers/release", { client: bob, returned: [], bought: [] })).status, 404);
  assert.equal(await count("listings"), 1);
  assert.equal(await count("receipts"), 0);
});

test("bad input is rejected", async () => {
  const request = serve();
  const offers = (change) => ["/v1/offers", { client: bob, ...change }];
  const cases = {
    "bad client": ["/v1/sales", { client: "nope", batch_id: "b1", items: [] }],
    "no items": ["/v1/sales", { client: alice, batch_id: "b1" }],
    "too many items": ["/v1/sales", { client: alice, batch_id: "b1", items: new Array(21).fill(sale("a")) }],
    "not json": ["/v1/sales", "[1]"],
    "broken json": ["/v1/sales", "{"],
    "bad market": ["/v1/sales", { client: alice, batch_id: "b1", market: "no spaces", items: [] }],
    "non-text market": ["/v1/sales", { client: alice, batch_id: "b1", market: 7, items: [] }],
    "offers without a client": offers({ client: undefined }),
    "offers with a bad client": offers({ client: "nope" }),
    "offers not json": ["/v1/offers", "[1]"],
    "offers broken json": ["/v1/offers", "{"],
    "offers with a bad limit": offers({ limit: "abc" }),
    "offers with a limit in text": offers({ limit: "8" }),
    "offers with a fractional limit": offers({ limit: 2.5 }),
    "offers with a bad category": offers({ category: "DRINK" }),
    "offers with a non-text category": offers({ category: 7 }),
    "offers with an empty market": offers({ market: "" }),
    "offers with a long market": offers({ market: "m".repeat(33) }),
    "offers with a non-text market": offers({ market: 7 }),
    "offers with categories in text": offers({ categories: "FOOD,TOOL" }),
    "offers with a non-text category in the list": offers({ categories: ["FOOD", 7] }),
    "offers with too many categories": offers({ categories: new Array(65).fill("FOOD") }),
    "offers with bought not a list": offers({ bought: "x" }),
    "offers with a bad listing id": offers({ bought: ["not safe!"] }),
    "offers with a non-text listing id": offers({ bought: [7] }),
    "offers with too many bought": offers({ bought: Array.from({ length: 31 }, (_, index) => `id${index}`) }),
  };
  for (const [name, [path, body]] of Object.entries(cases)) {
    assert.equal((await request(path, body)).status, 400, name);
  }
  for (const change of [{ limit: -5 }, { limit: 1000 }, { categories: ["not safe!"] }, { category: "" }, { bought: Array.from({ length: 30 }, (_, index) => `id${index}`) }, { market: null, category: null, limit: null, categories: null, bought: null }]) {
    assert.equal((await request(...offers(change))).status, 200, JSON.stringify(change));
  }
  assert.equal((await request("/v1/nope")).status, 404);
});

// A browser sends these types cross-site without asking first, so any page could post from its visitors' addresses.
test("a POST that isn't JSON is refused before anything else happens", async () => {
  const request = serve(100, 1, { IP_SALT: "salt", ADMIN_TOKEN: "secret" });
  const from = { "CF-Connecting-IP": "203.0.113.1" };
  const admin = { ...from, Authorization: "Bearer secret" };
  const refused = { status: 400, body: { error: "bad content type" } };
  const types = ["text/plain", "text/plain;charset=UTF-8", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x", "", "application/jsonp", "application/json; charset=utf-8, text/plain"];
  await request("/v1/sales", { client: "c".repeat(32), batch_id: "stock", items: [sale("a")] }, { "CF-Connecting-IP": "203.0.113.9" });
  for (const type of types) {
    assert.deepEqual(await request("/v1/sales", { client: alice, batch_id: "b1", items: [sale("a")] }, { ...from, "Content-Type": type }), refused, type);
    assert.deepEqual(await request("/v1/offers", { client: bob, bought: [] }, { ...from, "Content-Type": type }), refused, type);
    assert.deepEqual(await request("/v1/admin/ban", { key: alice }, { ...admin, "Content-Type": type }), refused, type);
  }
  assert.equal(await count("batches"), 1);
  assert.equal(await count("listings"), 1);
  assert.equal(await count("receipts"), 0);
  assert.equal(await count("banned_sellers"), 0);
  // Refused sales don't use up the sales limit, which allows one here.
  const post = (id, type) => request("/v1/sales", { client: alice, batch_id: id, items: [sale("a")] }, { ...from, "Content-Type": type });
  assert.equal((await post("b1", "Application/JSON; charset=UTF-8")).status, 200);
  assert.equal((await post("b2", "application/json")).status, 429);
});

test("rate limit is per address", async () => {
  const request = serve(1);
  const from = (address) => request("/v1/health", undefined, { "CF-Connecting-IP": address });
  assert.equal((await from("203.0.113.1")).status, 200);
  assert.equal((await from("203.0.113.2")).status, 200);
  assert.equal((await from("203.0.113.1")).status, 429);
});

test("a sale is refused without IP_SALT when the IP is known", async () => {
  const request = serve(100, 100, {});
  const response = await request("/v1/sales", { client: alice, batch_id: "b1", items: [sale("a")] }, { "CF-Connecting-IP": "203.0.113.1" });
  assert.equal(response.status, 500);
  assert.equal(await count("listings"), 0);
});

test("addresses in one IPv6 /64 share a rate limit", async () => {
  assert.equal(network("2001:db8:0:1::5"), "2001:db8:0:1::/64");
  assert.equal(network("2001:0db8:0000:0001:aaaa:bbbb:cccc:dddd"), "2001:db8:0:1::/64");
  assert.equal(network("::1"), "0:0:0:0::/64");
  assert.equal(network("203.0.113.1"), "203.0.113.1");
  const request = serve(1);
  assert.equal((await request("/v1/health", undefined, { "CF-Connecting-IP": "2001:db8:0:1::5" })).status, 200);
  assert.equal((await request("/v1/health", undefined, { "CF-Connecting-IP": "2001:db8:0:1::6" })).status, 429);
  assert.equal((await request("/v1/health", undefined, { "CF-Connecting-IP": "2001:db8:0:2::5" })).status, 200);
});

test("listing IDs don't reveal the seller", async () => {
  const f = setup();
  await f.post(alice, "b1", [sale("bread")]);
  const [offer] = await f.offers(bob);
  assert.ok(!offer.listing_id.includes(alice));
});

test("an absurd item value isn't listed", async () => {
  const f = setup();
  await f.post(alice, "b1", [{ ...sale("bread"), value: 1e300 }]);
  assert.equal(await count("listings"), 0);
});

test("a report with a malformed listing ID is refused", async () => {
  const f = setup();
  await assert.rejects(f.market.postOffers({ client: bob, bought: ["x".repeat(500)] }), BadRequest);
});

test("sales have their own stricter limit, and it leaves other requests alone", async () => {
  const request = serve(100, 1);
  const post = (id) => request("/v1/sales", { client: alice, batch_id: id, items: [sale("a")] }, { "CF-Connecting-IP": "203.0.113.1" });
  assert.equal((await post("b1")).status, 200);
  assert.equal((await post("b2")).status, 429);
  assert.equal(await count("batches"), 1);
  assert.equal((await request("/v1/offers", { client: bob }, { "CF-Connecting-IP": "203.0.113.1" })).status, 200);
  assert.equal((await request("/v1/sales", { client: bob, batch_id: "b3", items: [] }, { "CF-Connecting-IP": "203.0.113.2" })).status, 200);
});

test("sales over HTTP are tied to a hash of the address", async () => {
  const request = serve(100, 100, { IP_SALT: "pepper" });
  await request("/v1/sales", { client: alice, batch_id: "b1", items: [sale("a")] }, { "CF-Connecting-IP": "203.0.113.1" });
  const { source } = await db.prepare("SELECT source FROM listings").first();
  assert.match(source, /^[0-9a-f]{32}$/);
  assert.ok(!source.includes("203"));
  await request("/v1/sales", { client: bob, batch_id: "b1", items: [sale("a")] }, { "CF-Connecting-IP": "203.0.113.1" });
  await request("/v1/sales", { client: bob, batch_id: "b2", items: [sale("a")] }, { "CF-Connecting-IP": "203.0.113.2" });
  assert.equal((await db.prepare("SELECT COUNT(DISTINCT source) AS total FROM listings").first()).total, 2);
});

test("admin routes need the token, and don't exist without one", async () => {
  const closed = serve();
  assert.equal((await closed("/v1/admin/sellers")).status, 404);
  const request = serve(100, 100, { ADMIN_TOKEN: "secret" });
  const admin = { Authorization: "Bearer secret" };
  assert.equal((await request("/v1/admin/sellers")).status, 401);
  assert.equal((await request("/v1/admin/sellers", undefined, { Authorization: "Bearer wrong!" })).status, 401);
  assert.equal((await request("/v1/admin/ban", { key: alice })).status, 401);
  assert.equal(await count("banned_sellers"), 0);
  await request("/v1/sales", { client: alice, batch_id: "b1", items: [sale("a")] });
  assert.equal((await request("/v1/admin/sellers", undefined, admin)).body.sellers[0].source, alice);
  assert.deepEqual((await request("/v1/admin/ban", { key: alice }, admin)).body, { purged: 1 });
  assert.equal((await request("/v1/admin/unban", { key: alice }, admin)).status, 200);
  await request("/v1/sales", { client: alice, batch_id: "b2", items: [sale("a")] });
  assert.deepEqual((await request("/v1/admin/purge", { key: alice }, admin)).body, { purged: 1 });
  assert.equal((await request("/v1/admin/ban", { key: "nope" }, admin)).status, 400);
  assert.equal((await request("/v1/admin/nope", undefined, admin)).status, 404);
});

test("a database failure is a 500 the mod will retry", async () => {
  const broken = new Market({ prepare: () => { throw new Error("D1 limit exceeded"); } }, DEFAULT_CONFIG);
  const allow = { limit: async () => ({ success: true }) };
  const silenced = console.error;
  console.error = () => {};
  const request = new Request("https://market.test/v1/offers", { method: "POST", body: JSON.stringify({ client: bob }), headers: { "Content-Type": "application/json" } });
  const response = await handle(request, broken, allow, allow);
  console.error = silenced;
  assert.equal(response.status, 500);
  assert.deepEqual(await response.json(), { error: "server error" });
});

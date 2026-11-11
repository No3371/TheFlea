import { categoryDrift } from "./drift.ts";

const MAX_BATCH_ITEMS = 20;
const MAX_PAYLOAD_BYTES = 64 * 1024;
// Categories kept per item. Each one costs reads when pricing and a row when listing.
const MAX_TYPES = 5;
const MAX_SENT_TYPES = 64;
const MAX_OFFERS = 30;
const DEFAULT_OFFERS = 8;
const SECONDS_PER_DAY = 86400;
const SECONDS_PER_HOUR = 3600;
// How far past the wanted number of offers a pick looks, to find other sellers.
const CANDIDATES_PER_OFFER = 4;
// The most listings an offers request looks at, half in each direction from its random start, so a
// rare category costs no more in a large market. Minted offers fill what it doesn't find.
const MAX_WALK_ROWS = 500;
const MAX_LISTED_SELLERS = 50;
const MAX_LISTED_NAMES = 20;
const MINT_PREFIX = "mint:";
// Well above anything in the game, which tops out at a few thousand.
const MAX_ITEM_VALUE = 100_000;
// Tries at a sale before it's answered without listing or counting anything. See postSales.
const MAX_SALE_ATTEMPTS = 3;

// TypeHelper.MAJOR_TYPES_LIST and ALL_TYPES_LIST in game 0.46. Other names are dropped, and the
// major ones go first, because those are what the trader can be asked for.
const MAJOR_TYPES = ["FOOD", "MEDICAL", "MATERIAL", "HOUSEHOLD_GOOD", "SUBSTANCE", "WEAPON", "WATER", "ARMOR", "TOOL", "MODULE", "AMMUNITION"];
const GAME_TYPES = new Set([
  ...MAJOR_TYPES,
  "NOT_FOR_RESALE", "SUPPLY_CRATE", "MEDICAL_TOOL", "CONTRABAND", "RESTRICTED_1", "RESTRICTED_2", "RESTRICTED_3", "MAKESHIFT",
  "LOWTECH", "MIDLINE", "HIGHEND", "RARE", "LUXURY_ITEM", "ART", "WATER_RATION", "FARMING_SUPPLIES", "CHEMICAL_SUPPLIES",
  "FRESH_FOOD", "PROCESSED_FOOD", "TREAT", "BREVAGE", "DOCUMENT", "ACCESS_CARD", "SEED", "MODIFIED_SEED", "BASIC_MATERIAL",
  "REFINED_MATERIAL", "ENERGY_CELL", "NARCOTIC", "POISON", "STORAGE", "WATER_PURIFICATION_SUPPLY", "OXYGEN_SUPPLY", "ALCOHOL",
  "MELEE_WEAPON", "FIREARM", "MODDABLE", "MUNITION", "GUN_MOD", "NODE", "HYDROPONIC_MODULE", "ALARM_MODULE", "PRINTER_MODULE",
  "EVAPORATOR_MODULE", "MACHINE", "ORGAN", "FILTER", "LOTTERY", "JUNK",
]);

const SAFE_ID = /^[\p{L}\p{N}_.\-:$]{1,128}$/u;
const CLIENT_ID = /^[0-9a-fA-F]{32}$/;
const MARKET_CODE = /^[\p{L}\p{N}_-]{1,32}$/u;
const encoder = new TextEncoder();

// An error caused by the caller's input. Its text is safe to send back.
export class BadRequest extends Error {}

function randomHex(bytes: number): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(bytes)), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

export interface MarketConfig {
  allowSelf: boolean;
  minScale: number;
  categoryMinScale: number;
  floor: number;
  ceiling: number;
  fee: number;
  priceHalfLifeHours: number;
  volumeHalfLifeHours: number;
  demandWeight: number;
  driftAmplitude: number;
  driftSecret?: string;
  mintOffers: boolean;
  maxListingBytes: number;
  tradeMemoryDays: number;
  listingDays: number;
  minListingDays: number;
  maxUnitsPerItem: number;
  maxLiveListings: number;
  maxListingsPerDay: number;
  maxUnitsPerDay: number;
  sourceQuotaFactor: number;
  minRealShare: number;
  maxRealShare: number;
  maxSellerShare: number;
  batchMemoryDays: number;
  maxBatches: number;
}

export const DEFAULT_CONFIG: MarketConfig = {
  allowSelf: false,
  minScale: 20,
  categoryMinScale: 300,
  floor: 0.5,
  ceiling: 1.5,
  fee: 0.1,
  priceHalfLifeHours: 2,
  volumeHalfLifeHours: 12,
  demandWeight: 0.25,
  // How far each major category drifts on its own, in the same log units as pressure: e^0.22 is about
  // 0.80x to 1.25x. It has to clear the trader's cut, or selling to him would never beat a normal customer.
  // There's no drift without driftSecret, which comes from the DRIFT_SALT secret.
  driftAmplitude: 0.22,
  // Fills the slots real listings leave with minted offers, copied from random listings' values and types.
  mintOffers: true,
  // Well under the free plan's 500 MB, since rows and indexes take space too.
  maxListingBytes: 300 * 1024 * 1024,
  tradeMemoryDays: 7,
  // The longest a listing waits for a buyer, and how long a receipt for one sent to a buyer waits for its report.
  listingDays: 14,
  // A listing lasts from listingDays down to this, the more live listings its seller holds. See lifetime.
  minListingDays: 3,
  maxUnitsPerItem: 999,
  // Per client ID. Client IDs are free to make, so each IP address gets sourceQuotaFactor times these too.
  // A seller holding maxLiveListings gets the shortest listings. Past the daily limit a sale isn't listed.
  // Past the unit limit it doesn't move prices.
  maxLiveListings: 100,
  maxListingsPerDay: 200,
  maxUnitsPerDay: 300,
  sourceQuotaFactor: 2,
  // Each visit picks its share of real offers from this range. Minted offers fill the rest.
  minRealShare: 0.25,
  maxRealShare: 0.5,
  // The share of those real offers one IP address can take.
  maxSellerShare: 0.5,
  // Batches are remembered so a resent one gets the same answer. Players resend only when a visit
  // broke off, so a week is plenty. Past maxBatches, the oldest go first.
  batchMemoryDays: 7,
  maxBatches: 20_000,
};

export interface PriceEntry {
  item_id: string;
  sell_multiplier: number;
  buy_multiplier: number;
}

// Answers a posted batch. It is stored, so a repeated batch gets the same answer.
export interface SalesResponse {
  batch_id: string;
  listed: number;
  prices: PriceEntry[];
}

export interface Offer {
  listing_id: string;
  item_id: string;
  buy_multiplier: number;
  payload: string;
  // A minted offer has no item yet: the buyer's game picks one of this value and these types.
  // A buyer whose game doesn't have a listing's item does the same with that listing.
  minted?: boolean;
  value: number;
  types: string[];
}

export interface OffersResponse {
  offers: Offer[];
  // The current multiplier of each category the caller asked about.
  categories: Record<string, number>;
  // The share taken from a seller's payout.
  fee: number;
}

const TRADE_TABLES = {
  item: { table: "trades", column: "item_id" },
  category: { table: "category_trades", column: "type" },
};

interface TradeTotals {
  name: string;
  net: number;
  volume: number;
  updated_at: number;
}

interface SentListing {
  id: string;
  item_id: string;
  payload: string;
  types: string;
  item_value: number;
  source: string;
}

// A listing an offers request found. A minted offer copies its value and types.
interface Candidate {
  id: string;
  source: string;
  item_value: number;
  types: string;
}

// An item that can become a listing: it's readable, fits and is worth something.
interface Sale {
  index: number;
  itemId: string;
  units: number;
  types: string[];
  payload: string;
  bytes: number;
  value: number;
}

interface QuotaRow {
  key: string;
  day: number;
  listings: number;
  units: number;
  live: number;
}

// A sale past the seller's daily units is still listed, but doesn't count toward prices.
interface PlannedSale {
  sale: Sale;
  counted: boolean;
  expiresAt: number;
}

export interface SellerSummary {
  source: string;
  listings: number;
  listings_today: number;
  units_today: number;
  clients: string[];
  names: string[];
}

type Body = Record<string, unknown>;

function round4(value: number): number {
  return Math.round(value * 1e4) / 1e4;
}

function parseClient(value: unknown): string {
  if (typeof value !== "string" || !CLIENT_ID.test(value)) {
    throw new BadRequest("bad client");
  }
  return value;
}

// Markets are separate worlds with their own listings and prices. Requests without a code use market 0.
export function parseMarket(value: unknown): string {
  if (value === undefined || value === null) {
    return "0";
  }
  const market = typeof value === "string" ? value.trim().toLowerCase() : "";
  if (!MARKET_CODE.test(market)) {
    throw new BadRequest("bad market");
  }
  return market;
}

function parseIdList(value: unknown): string[] {
  if (value === undefined || value === null) {
    return [];
  }
  if (!Array.isArray(value) || value.length > MAX_OFFERS || !value.every((id) => typeof id === "string" && SAFE_ID.test(id))) {
    throw new BadRequest("bad listing ids");
  }
  // The purchase queries join on this list, so a repeated ID would count twice.
  return [...new Set<string>(value)];
}

function parseCategory(value: unknown): string {
  if (value === undefined || value === null || value === "") {
    return "";
  }
  if (typeof value !== "string" || !GAME_TYPES.has(value)) {
    throw new BadRequest("bad category");
  }
  return value;
}

function parseLimit(value: unknown): number {
  if (value === undefined || value === null) {
    return DEFAULT_OFFERS;
  }
  if (typeof value !== "number" || !Number.isInteger(value)) {
    throw new BadRequest("bad limit");
  }
  return Math.max(1, Math.min(value, MAX_OFFERS));
}

// Names the game doesn't have are dropped rather than refused, as are repeats.
function parseCategoryList(value: unknown): string[] {
  if (value === undefined || value === null) {
    return [];
  }
  if (!Array.isArray(value) || value.length > MAX_SENT_TYPES || !value.every((name) => typeof name === "string")) {
    throw new BadRequest("bad categories");
  }
  return [...new Set<string>(value)].filter((name) => GAME_TYPES.has(name)).slice(0, MAJOR_TYPES.length);
}

function parseUnits(value: unknown, max: number): number {
  return typeof value === "number" && Number.isInteger(value) && value >= 1 && value <= max ? value : 1;
}

function parseTypes(value: unknown): string[] | null {
  if (!Array.isArray(value) || value.length > MAX_SENT_TYPES || !value.every((type) => typeof type === "string")) {
    return null;
  }
  const known = [...new Set<string>(value)].filter((type) => GAME_TYPES.has(type));
  const major = known.filter((type) => MAJOR_TYPES.includes(type));
  return [...major, ...known.filter((type) => !MAJOR_TYPES.includes(type))].slice(0, MAX_TYPES);
}

function parseSale(item: Body, itemId: string, types: string[] | null, index: number, maxUnits: number): Sale | null {
  const { payload, value } = item;
  if (!types || typeof payload !== "string" || payload === "" || typeof value !== "number" || !Number.isFinite(value) || value <= 0 || value > MAX_ITEM_VALUE) {
    return null;
  }
  const bytes = encoder.encode(payload).length;
  if (bytes > MAX_PAYLOAD_BYTES) {
    return null;
  }
  return { index, itemId, units: parseUnits(item.count, maxUnits), types, payload, bytes, value };
}

function byName(rows: TradeTotals[]): Map<string, TradeTotals> {
  return new Map(rows.map((row) => [row.name, row]));
}

// Stores listings and recent trade counts and hands out price multipliers.
// It never holds money: each game works out the actual prices itself.
// D1 has no read-then-write transactions. Each statement or batch is atomic, but what one batch read can
// be out of date by the next, so a decision made from a read is checked again inside the batch that acts on it.
export class Market {
  private readonly db: D1Database;
  private readonly config: MarketConfig;
  private readonly clock: () => number;

  constructor(db: D1Database, config: MarketConfig, clock: () => number = () => Date.now() / 1000) {
    this.db = db;
    this.config = config;
    this.clock = clock;
  }

  private trades(kind: keyof typeof TRADE_TABLES, market: string, names: string[]): D1PreparedStatement {
    const { table, column } = TRADE_TABLES[kind];
    return this.db
      .prepare(
        `SELECT ${column} AS name, net, volume, updated_at FROM ${table}
         WHERE market = ?1 AND ${column} IN (SELECT value FROM json_each(?2))`,
      )
      .bind(market, JSON.stringify(names));
  }

  // Fades the stored totals up to the new trade's time, then adds it.
  private addToTotals(kind: keyof typeof TRADE_TABLES): string {
    const { column } = TRADE_TABLES[kind];
    const { priceHalfLifeHours, volumeHalfLifeHours } = this.config;
    const age = "(excluded.updated_at - updated_at) / 3600.0";
    return `ON CONFLICT (market, ${column}) DO UPDATE SET
      net = net * pow(0.5, ${age} / ${priceHalfLifeHours}) + excluded.net,
      volume = volume * pow(0.5, ${age} / ${volumeHalfLifeHours}) + excluded.volume,
      updated_at = excluded.updated_at`;
  }

  // Net demand after fading, scaled by how much usually trades within one price half-life,
  // so it takes more units to move the price of something busy than something quiet.
  private pressure(totals: TradeTotals | undefined, now: number, justSold: number, minScale: number): number {
    const { priceHalfLifeHours, volumeHalfLifeHours } = this.config;
    const age = totals ? Math.max(0, now - totals.updated_at) / SECONDS_PER_HOUR : 0;
    const net = (totals?.net ?? 0) * 0.5 ** (age / priceHalfLifeHours) - justSold;
    const volume = (totals?.volume ?? 0) * 0.5 ** (age / volumeHalfLifeHours) + justSold;
    return net / Math.max(minScale, (volume * priceHalfLifeHours) / volumeHalfLifeHours);
  }

  private categoryPressures(types: string[], totals: Map<string, TradeTotals>, now: number, justSold?: Map<string, number>): number[] {
    return types.map((type) => this.pressure(totals.get(type), now, justSold?.get(type) ?? 0, this.config.categoryMinScale));
  }

  // The item's own pressure plus the average across its categories, so an item
  // with many categories isn't pushed further than an item with one.
  private price(itemId: string, totals: TradeTotals | undefined, now: number, justSold = 0, categories: number[] = [], drift = 0): PriceEntry {
    const { minScale, ceiling, floor, fee } = this.config;
    const category = categories.length > 0 ? categories.reduce((sum, value) => sum + value, 0) / categories.length : 0;
    const buy = Math.min(ceiling, Math.max(floor, Math.exp(this.pressure(totals, now, justSold, minScale) + category + drift)));
    return { item_id: itemId, sell_multiplier: round4(buy * (1 - fee)), buy_multiplier: round4(buy) };
  }

  // Only the major categories drift, since those are what the trader can be asked for and what the game
  // shows rates for.
  private drift(market: string, types: string[], now: number): Promise<Map<string, number>> {
    const { driftSecret, driftAmplitude } = this.config;
    return categoryDrift(driftSecret, driftAmplitude, market, types.filter((type) => MAJOR_TYPES.includes(type)), now);
  }

  // The average drift of the item's major categories, so an item that also has minor ones isn't moved less.
  private itemDrift(types: string[], drift: Map<string, number>): number {
    const moved = types.filter((type) => drift.has(type)).map((type) => drift.get(type) ?? 0);
    return moved.length > 0 ? moved.reduce((sum, value) => sum + value, 0) / moved.length : 0;
  }

  // Days a new listing lasts when its seller already holds `live` of them: the longest at none, falling
  // slowly at first and reaching the shortest at `most`. Nobody is refused, so the more a seller lists,
  // the sooner each listing goes, which bounds what one seller holds without having to count it exactly.
  private lifetime(live: number, most: number): number {
    const { listingDays, minListingDays } = this.config;
    return minListingDays + (listingDays - minListingDays) * (1 - Math.min(1, live / most) ** 2);
  }

  // Decides which sales reach the market and how long each lasts. Client IDs are free to make, so the
  // IP address has limits of its own, and the shorter lifetime of the two wins. Sales left out are still paid for.
  private planSales(sales: Sale[], client: string, source: string, quota: QuotaRow[], today: number, now: number): PlannedSale[] {
    const { maxLiveListings, maxListingsPerDay, maxUnitsPerDay, sourceQuotaFactor } = this.config;
    const sellers = [...new Set([client, source])].map((key) => {
      const use = quota.find((row) => row.key === key && row.day === today);
      // Before today's cleanup or sale, yesterday's row holds the latest count.
      const latest = use ?? quota.find((row) => row.key === key && row.day === today - 1);
      return { factor: key === client ? 1 : sourceQuotaFactor, listings: use?.listings ?? 0, units: use?.units ?? 0, live: latest?.live ?? 0 };
    });
    const planned: PlannedSale[] = [];
    for (const sale of sales) {
      if (sellers.some(({ factor, listings }) => listings + 1 > maxListingsPerDay * factor)) {
        continue;
      }
      const counted = sellers.every(({ factor, units }) => units + sale.units <= maxUnitsPerDay * factor);
      const days = Math.min(...sellers.map(({ factor, live }) => this.lifetime(live, maxLiveListings * factor)));
      for (const seller of sellers) {
        seller.live += 1;
        seller.listings += 1;
        seller.units += counted ? sale.units : 0;
      }
      planned.push({ sale, counted, expiresAt: now + days * SECONDS_PER_DAY });
    }
    return planned;
  }

  // Records a batch and lists its items. Only items that become listings move prices: anything
  // unreadable, oversized, worthless or over the seller's daily limits gets a price and nothing else.
  async postSales(body: Body, sourceValue?: string): Promise<SalesResponse> {
    const market = parseMarket(body.market);
    const client = parseClient(body.client);
    const source = sourceValue ?? client;
    const batchId = body.batch_id;
    if (typeof batchId !== "string" || !SAFE_ID.test(batchId)) {
      throw new BadRequest("bad batch_id");
    }
    const items = body.items;
    if (!Array.isArray(items) || items.length > MAX_BATCH_ITEMS) {
      throw new BadRequest("bad items");
    }

    const now = this.clock();
    const today = Math.floor(now / SECONDS_PER_DAY);
    const itemTypes = new Map<string, string[]>();
    const quoted = new Set<string>();
    const sales: Sale[] = [];
    items.forEach((raw: unknown, index: number) => {
      const item = (raw !== null && typeof raw === "object" ? raw : {}) as Body;
      const itemId = item.item_id;
      if (typeof itemId !== "string" || !SAFE_ID.test(itemId)) {
        return;
      }
      quoted.add(itemId);
      const types = parseTypes(item.types);
      if (types && !itemTypes.has(itemId)) {
        itemTypes.set(itemId, types);
      }
      const sale = parseSale(item, itemId, types, index, this.config.maxUnitsPerItem);
      if (sale) {
        sales.push(sale);
      }
    });

    // Nothing to price or list, so there's nothing worth remembering either.
    if (quoted.size === 0) {
      return { batch_id: batchId, listed: 0, prices: [] };
    }

    const drift = await this.drift(market, [...itemTypes.values()].flat(), now);
    const seenQuery = this.db
      .prepare("SELECT response FROM batches WHERE market = ? AND client = ? AND batch_id = ?")
      .bind(market, client, batchId);
    const itemIds = [...quoted].sort();
    const sellerKeys = JSON.stringify([client, source]);
    // The seller's limits are decided from this read, so the write batch first checks that the read
    // still holds, and a sale that lost a race with another one from the same seller is planned again.
    // The last attempt lists and counts nothing, so it needs no check and the seller is always answered.
    for (let attempt = 1; ; attempt++) {
      const lastAttempt = attempt === MAX_SALE_ATTEMPTS;
      const [seen, itemTotals, categoryTotals, used, banned] = await this.db.batch<Record<string, unknown>>([
        seenQuery,
        this.trades("item", market, itemIds),
        this.trades("category", market, [...new Set([...itemTypes.values()].flat())]),
        // Yesterday too, so a sale made just across midnight still shows up when checking the read,
        // and so the live count is known before today's cleanup has run.
        this.db
          .prepare("SELECT key, day, listings, units, live FROM seller_quota WHERE key IN (SELECT value FROM json_each(?2)) AND day >= ?1 - 1")
          .bind(today, sellerKeys),
        this.db.prepare("SELECT key FROM banned_sellers WHERE key IN (SELECT value FROM json_each(?1))").bind(sellerKeys),
      ]);
      const stored = seen.results[0]?.response;
      if (typeof stored === "string") {
        return JSON.parse(stored);
      }

      const quota = used.results as unknown as QuotaRow[];
      // Every sale that lists anything adds to these rows, so their total changes whenever this seller's
      // usage grows. The cleanup only changes live counts, which a lost race just makes a little stale.
      const version = quota.reduce((total, row) => total + row.listings, 0);
      const planned = banned.results.length > 0 || lastAttempt ? [] : this.planSales(sales, client, source, quota, today, now);
      const sold = new Map<string, number>();
      const soldByCategory = new Map<string, number>();
      const listings: D1PreparedStatement[] = [];
      const listingBytes = planned.reduce((total, { sale }) => total + sale.bytes, 0);
      let listingUnits = 0;

      // A full market still counts and pays for a sale. It just doesn't list the items, so nobody
      // loses a payout, and offers keep working until old listings expire and make room. Whether it's
      // full is decided inside the write batch: every statement below tests storage as it was before
      // the batch, because the one that changes storage comes last.
      const room = `(SELECT listing_bytes FROM storage) + ${listingBytes} <= ${this.config.maxListingBytes}`;
      const insertListing = this.db.prepare(
        `INSERT INTO listings (market, id, seller, source, item_id, payload, units, item_value, types, created_at, expires_at)
         SELECT ?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11 WHERE ${room}`,
      );
      for (const { sale, counted, expiresAt } of planned) {
        const units = counted ? sale.units : 0;
        if (counted) {
          sold.set(sale.itemId, (sold.get(sale.itemId) ?? 0) + units);
          for (const type of sale.types) {
            soldByCategory.set(type, (soldByCategory.get(type) ?? 0) + units);
          }
        }
        listingUnits += units;
        // Random, because buyers see listing IDs and the client ID is all that identifies a seller,
        // and because offers take the primary key's order as a random one.
        const id = randomHex(12);
        listings.push(insertListing.bind(market, id, client, source, sale.itemId, sale.payload, units, sale.value, JSON.stringify(sale.types), now, expiresAt));
      }

      const byItem = byName(itemTotals.results as unknown as TradeTotals[]);
      const byCategory = byName(categoryTotals.results as unknown as TradeTotals[]);
      const response = (listed: number): string =>
        JSON.stringify({
          batch_id: batchId,
          listed,
          prices: itemIds.map((itemId) =>
            this.price(
              itemId,
              byItem.get(itemId),
              now,
              sold.get(itemId) ?? 0,
              this.categoryPressures(itemTypes.get(itemId) ?? [], byCategory, now, soldByCategory),
              this.itemDrift(itemTypes.get(itemId) ?? [], drift),
            ),
          ),
        } satisfies SalesResponse);

      // The answer is chosen inside the batch, and the stored copy is what's returned.
      const statements = [
        this.db
          .prepare(
            `INSERT INTO batches (market, client, batch_id, response, created_at)
             SELECT ?1, ?2, ?3, CASE WHEN ${room} THEN ?4 ELSE ?5 END, ?6
             RETURNING response`,
          )
          .bind(market, client, batchId, response(planned.length), response(0), now),
      ];
      if (planned.length > 0) {
        // Inserting storage's one row again fails, which undoes the whole batch, if another sale by this
        // seller has gone in since the read.
        statements.push(
          this.db
            .prepare(
              `INSERT INTO storage (id, listing_bytes) SELECT 1, 0
               WHERE (SELECT COALESCE(SUM(listings), 0) FROM seller_quota WHERE key IN (SELECT value FROM json_each(?1)) AND day >= ?2 - 1) <> ?3`,
            )
            .bind(sellerKeys, today, version),
        );
      }
      statements.push(...listings);
      if (planned.length > 0) {
        // A key's first row of the day carries on from yesterday's live count, until the cleanup counts again.
        statements.push(
          this.db
            .prepare(
              `INSERT INTO seller_quota (key, day, listings, units, live)
               SELECT value, ?1, ?2, ?3, COALESCE((SELECT q.live FROM seller_quota q WHERE q.key = value AND q.day = ?1 - 1), 0) + ?2
               FROM json_each(?4) WHERE true
               ON CONFLICT (key, day) DO UPDATE SET
                 listings = listings + excluded.listings,
                 units = units + excluded.units,
                 live = live + excluded.listings`,
            )
            .bind(today, planned.length, listingUnits, JSON.stringify([...new Set([client, source])])),
        );
      }
      if (sold.size > 0) {
        statements.push(
          this.db
            .prepare(
              `INSERT INTO trades (market, item_id, net, volume, updated_at)
               SELECT ?1, value ->> 0, -(value ->> 1), value ->> 1, ?2 FROM json_each(?3) WHERE true
               ${this.addToTotals("item")}`,
            )
            .bind(market, now, JSON.stringify([...sold])),
        );
      }
      if (soldByCategory.size > 0) {
        statements.push(
          this.db
            .prepare(
              `INSERT INTO category_trades (market, type, net, volume, updated_at)
               SELECT ?1, value ->> 0, -(value ->> 1), value ->> 1, ?2 FROM json_each(?3) WHERE true
               ${this.addToTotals("category")}`,
            )
            .bind(market, now, JSON.stringify([...soldByCategory])),
        );
      }
      if (listingBytes !== 0) {
        statements.push(this.db.prepare(`UPDATE storage SET listing_bytes = listing_bytes + ?1 WHERE ${room}`).bind(listingBytes));
      }
      try {
        const [answer] = await this.db.batch<{ response: string }>(statements);
        return JSON.parse(answer.results[0].response);
      } catch (error) {
        // The same batch arrived twice at once. The batches row goes first, so the loser changed nothing.
        const [again] = await this.db.batch<{ response: string }>([seenQuery]);
        const winner = again.results[0];
        if (winner) {
          return JSON.parse(winner.response);
        }
        if (lastAttempt) {
          throw error;
        }
      }
    }
  }

  // Records what the caller bought from its last offers, then sends random listings, which leave the
  // market as they're sent. Minted offers fill the other slots. They aren't stored anywhere, and buying
  // one doesn't move prices.
  async postOffers(body: Body): Promise<OffersResponse> {
    const market = parseMarket(body.market);
    const client = parseClient(body.client);
    const category = parseCategory(body.category);
    const limit = parseLimit(body.limit);
    const requested = parseCategoryList(body.categories);
    const bought = parseIdList(body.bought);

    const now = this.clock();
    // Done first, so the prices sent below include these purchases. A retried request finds the
    // receipts already gone, so it counts nothing again.
    if (bought.length > 0) {
      await this.recordPurchases(client, bought, now);
    }
    // Most slots go to minted offers, which are the buyer's own items at their own prices, so a flood
    // of listings can't dominate a visit. Real listings only go past their share if no listing can be copied.
    const { allowSelf, minRealShare, maxRealShare, maxSellerShare, mintOffers } = this.config;
    const fewest = Math.ceil(limit * minRealShare);
    const realSlots = allowSelf ? limit : fewest + Math.floor(Math.random() * (Math.ceil(limit * maxRealShare) - fewest + 1));
    const perSeller = allowSelf ? limit : Math.ceil(realSlots * maxSellerShare);
    const found = await this.candidates(market, client, category, limit * CANDIDATES_PER_OFFER, now);
    const copyable = mintOffers ? Math.min(limit, found.length) : 0;
    const seats: Record<string, number> = {};
    const picked: Candidate[] = [];
    for (const listing of found) {
      if (picked.length < Math.max(realSlots, limit - copyable) && (seats[listing.source] ?? 0) < perSeller) {
        seats[listing.source] = (seats[listing.source] ?? 0) + 1;
        picked.push(listing);
      }
    }
    const visit = randomHex(8);
    // A listing another buyer took since the read is left out, and a minted offer takes its slot.
    const sent = picked.length > 0 ? await this.send(market, client, visit, picked.map((listing) => listing.id), now) : [];
    // Minted offers copy the listings found but not sent first, then the sent ones, so a visit can
    // still mint from the listings it takes.
    const templates = mintOffers ? [...found.filter((listing) => !picked.includes(listing)), ...picked] : [];
    const mints = templates
      .slice(0, Math.max(0, limit - sent.length))
      .map((template, index) => ({ id: `${MINT_PREFIX}${visit}:${index}`, value: template.item_value, types: JSON.parse(template.types) as string[] }));
    const types = new Map<string, string[]>(sent.map((listing) => [listing.id, JSON.parse(listing.types)]));
    const [itemTotals, categoryTotals] = await this.db.batch<TradeTotals>([
      this.trades("item", market, [...new Set(sent.map((listing) => listing.item_id))]),
      this.trades("category", market, [...new Set([...types.values(), ...mints.map((mint) => mint.types)].flat().concat(requested))]),
    ]);
    const byItem = byName(itemTotals.results);
    const byCategory = byName(categoryTotals.results);
    const drift = await this.drift(market, [...types.values(), ...mints.map((mint) => mint.types)].flat().concat(requested), now);
    const priced = (itemId: string, listingTypes: string[]) =>
      this.price(itemId, byItem.get(itemId), now, 0, this.categoryPressures(listingTypes, byCategory, now), this.itemDrift(listingTypes, drift)).buy_multiplier;
    return {
      offers: [
        ...sent.map((listing): Offer => {
          const listingTypes = types.get(listing.id) ?? [];
          return {
            listing_id: listing.id,
            item_id: listing.item_id,
            buy_multiplier: priced(listing.item_id, listingTypes),
            payload: listing.payload,
            value: listing.item_value,
            types: listingTypes,
          };
        }),
        ...mints.map((mint): Offer => ({
          listing_id: mint.id,
          item_id: "",
          buy_multiplier: priced("", mint.types),
          payload: "",
          minted: true,
          value: mint.value,
          types: mint.types,
        })),
      ],
      categories: Object.fromEntries(
        requested.map((name) => [name, this.price(name, undefined, now, 0, this.categoryPressures([name], byCategory, now), this.itemDrift([name], drift)).buy_multiplier]),
      ),
      fee: this.config.fee,
    };
  }

  // Walks the primary key from a random point, in both directions, which is a random order because
  // listing IDs are random. Each direction looks at no more than half of MAX_WALK_ROWS, so the cost
  // doesn't depend on how many listings the market holds, even for a rare category.
  private async candidates(market: string, client: string, category: string, wanted: number, now: number): Promise<Candidate[]> {
    const start = randomHex(12);
    const walk = (side: ">=" | "<") =>
      this.db
        .prepare(
          `SELECT id, source, item_value, types FROM (
             SELECT id, seller, source, item_value, types, expires_at FROM listings
             WHERE market = ?1 AND id ${side} ?2 ORDER BY id LIMIT ?3
           )
           WHERE expires_at > ?4 AND (?5 OR seller <> ?6) AND (?7 = '' OR instr(types, ?7) > 0)
           LIMIT ?8`,
        )
        // Category names are the game's own and have no quotes, so the quoted name only matches a whole
        // one. D1 counts each json_each row as read, which would double the cost of the walk.
        .bind(market, start, MAX_WALK_ROWS / 2, now, this.config.allowSelf ? 1 : 0, client, category && JSON.stringify(category), wanted);
    const [after, before] = await this.db.batch<Candidate>([walk(">="), walk("<")]);
    return [...after.results, ...before.results];
  }

  // Takes listings out of the market for the caller and leaves one receipt for all of them. One batch,
  // so no two callers are sent the same listing: whatever another one took first is no longer there.
  // The batch's later statements find its listings through the receipt it has just written.
  private async send(market: string, client: string, visit: string, ids: string[], now: number): Promise<SentListing[]> {
    const receipt = "json_each((SELECT listings FROM receipts WHERE client = ?1 AND visit = ?2)) r";
    const results = await this.db.batch<SentListing>([
      this.db
        .prepare(
          `INSERT INTO receipts (client, visit, market, sent_at, listings)
           SELECT ?1, ?2, ?3, ?4, listings FROM (
             SELECT COUNT(*) AS taken, json_group_object(id, json_array(item_id, units, json(types), created_at)) AS listings
             FROM listings WHERE market = ?3 AND id IN (SELECT value FROM json_each(?5)) AND expires_at > ?4
           ) WHERE taken > 0`,
        )
        .bind(client, visit, market, now, JSON.stringify(ids)),
      this.db
        .prepare(
          `UPDATE storage SET listing_bytes = MAX(0, listing_bytes - (
             SELECT COALESCE(SUM(LENGTH(CAST(l.payload AS BLOB))), 0)
             FROM ${receipt} CROSS JOIN listings l ON l.market = ?3 AND l.id = r.key))
           WHERE EXISTS (SELECT 1 FROM receipts WHERE client = ?1 AND visit = ?2)`,
        )
        .bind(client, visit, market),
      this.db
        .prepare(`DELETE FROM listings WHERE market = ?3 AND id IN (SELECT r.key FROM ${receipt}) RETURNING id, item_id, payload, item_value, source, types`)
        .bind(client, visit, market),
    ]);
    return results[results.length - 1].results;
  }

  // Records purchases of listings sent to this client, in the market each was sent from, and takes them
  // out of their receipts in the same batch, so a repeated report matches nothing. An ID not in a receipt
  // of this client's, such as a minted offer's, an expired receipt's or another client's, counts nothing.
  // A client has few receipts, so all of them are read through the primary key.
  private async recordPurchases(client: string, bought: string[], now: number): Promise<void> {
    // A receipt's entries are [item_id, units, types, created_at].
    const units = "(e.value ->> 1)";
    // A purchase first cancels whatever is left of the listing's own sale, because that supply
    // is now gone. Only the demand weight is new demand, so buying a stack can't cause a rebound.
    const demand = `${units} * pow(0.5, (?3 - (e.value ->> 3)) / 3600.0 / ${this.config.priceHalfLifeHours}) + ${units} * ${this.config.demandWeight}`;
    const reported = "e.key IN (SELECT value FROM json_each(?2))";
    const fresh = "client = ?1 AND sent_at >= ?3";
    const ids = JSON.stringify(bought);
    const cutoff = now - this.config.listingDays * SECONDS_PER_DAY;
    await this.db.batch([
      this.db
        .prepare(
          `INSERT INTO trades (market, item_id, net, volume, updated_at)
           SELECT r.market, e.value ->> 0, SUM(${demand}), SUM(${units}), ?3
           FROM receipts r CROSS JOIN json_each(r.listings) e
           WHERE r.client = ?1 AND r.sent_at >= ?4 AND ${reported}
           GROUP BY r.market, e.value ->> 0
           ${this.addToTotals("item")}`,
        )
        .bind(client, ids, now, cutoff),
      this.db
        .prepare(
          `INSERT INTO category_trades (market, type, net, volume, updated_at)
           SELECT r.market, t.value, SUM(${demand}), SUM(${units}), ?3
           FROM receipts r CROSS JOIN json_each(r.listings) e CROSS JOIN json_each(e.value -> 2) t
           WHERE r.client = ?1 AND r.sent_at >= ?4 AND ${reported}
           GROUP BY r.market, t.value
           ${this.addToTotals("category")}`,
        )
        .bind(client, ids, now, cutoff),
      // A receipt with every listing reported goes. One with some left keeps those, to be reported later.
      this.db
        .prepare(`DELETE FROM receipts WHERE ${fresh} AND NOT EXISTS (SELECT 1 FROM json_each(listings) e WHERE NOT ${reported})`)
        .bind(client, ids, cutoff),
      this.db
        .prepare(
          `UPDATE receipts SET listings = (SELECT json_group_object(e.key, json(e.value)) FROM json_each(listings) e WHERE NOT ${reported})
           WHERE ${fresh} AND EXISTS (SELECT 1 FROM json_each(listings) e WHERE ${reported})`,
        )
        .bind(client, ids, cutoff),
    ]);
  }

  // Trade totals have faded to nothing by the end of their memory. A receipt nobody reported goes
  // after as long as the longest listing lasts. Then each seller key's live listings are counted for
  // its row today, which sales read to set how long new listings last.
  async prune(): Promise<void> {
    const now = this.clock();
    const today = Math.floor(now / SECONDS_PER_DAY);
    const tradeCutoff = now - this.config.tradeMemoryDays * SECONDS_PER_DAY;
    const keys = "SELECT seller AS key FROM listings UNION ALL SELECT source FROM listings WHERE source <> seller";
    // Receipts go in a batch of their own, so a failure in either batch doesn't stop the other.
    const receipts = this.db.batch([this.db.prepare("DELETE FROM receipts WHERE sent_at < ?").bind(now - this.config.listingDays * SECONDS_PER_DAY)]);
    const rest = this.db.batch([
      this.db.prepare("DELETE FROM listings WHERE expires_at <= ?").bind(now),
      this.db.prepare("DELETE FROM trades WHERE updated_at < ?").bind(tradeCutoff),
      this.db.prepare("DELETE FROM category_trades WHERE updated_at < ?").bind(tradeCutoff),
      this.db.prepare("DELETE FROM batches WHERE created_at < ?").bind(now - this.config.batchMemoryDays * SECONDS_PER_DAY),
      this.db
        .prepare("DELETE FROM batches WHERE created_at <= (SELECT created_at FROM batches ORDER BY created_at DESC LIMIT 1 OFFSET ?)")
        .bind(this.config.maxBatches),
      this.db.prepare("DELETE FROM seller_quota WHERE day < ?").bind(today),
      this.db.prepare(`UPDATE seller_quota SET live = 0 WHERE day = ?1 AND live <> 0 AND key NOT IN (${keys})`).bind(today),
      this.db
        .prepare(
          `INSERT INTO seller_quota (key, day, listings, units, live)
           SELECT key, ?1, 0, 0, COUNT(*) FROM (${keys}) WHERE true GROUP BY key
           ON CONFLICT (key, day) DO UPDATE SET live = excluded.live`,
        )
        .bind(today),
      // Corrects any drift once a day, when this full scan is affordable.
      this.db.prepare("UPDATE storage SET listing_bytes = (SELECT COALESCE(SUM(LENGTH(CAST(payload AS BLOB))), 0) FROM listings)"),
    ]);
    const failed = (await Promise.allSettled([rest, receipts])).find((result) => result.status === "rejected");
    if (failed) {
      throw failed.reason;
    }
  }

  // The sellers holding the most listings in a market. An admin tool, so a full scan is fine.
  async sellers(marketValue: unknown): Promise<SellerSummary[]> {
    const market = parseMarket(marketValue);
    const [result] = await this.db.batch<Omit<SellerSummary, "clients" | "names"> & { clients: string; names: string }>([
      this.db
        .prepare(
          `SELECT l.source, COUNT(*) AS listings,
                  COALESCE(q.listings, 0) AS listings_today, COALESCE(q.units, 0) AS units_today,
                  json_group_array(DISTINCT l.seller) AS clients,
                  json_group_array(DISTINCT CASE WHEN json_valid(l.payload) THEN json_extract(l.payload, '$.seller') END) AS names
           FROM listings l LEFT JOIN seller_quota q ON q.key = l.source AND q.day = ?2
           WHERE l.market = ?1
           GROUP BY l.source
           ORDER BY listings DESC LIMIT ?3`,
        )
        .bind(market, Math.floor(this.clock() / SECONDS_PER_DAY), MAX_LISTED_SELLERS),
    ]);
    return result.results.map((row) => ({
      ...row,
      clients: (JSON.parse(row.clients) as string[]).slice(0, MAX_LISTED_NAMES),
      names: (JSON.parse(row.names) as (string | null)[]).filter((name): name is string => !!name).slice(0, MAX_LISTED_NAMES),
    }));
  }

  // Removes everything a client ID or IP hash has listed, in every market. Receipts stay: their
  // listings had already left the market, so a buyer's later report of one still counts.
  private purgeStatements(key: string): D1PreparedStatement[] {
    const mine = "(seller = ?1 OR source = ?1)";
    return [
      this.db
        .prepare(`UPDATE storage SET listing_bytes = MAX(0, listing_bytes - (SELECT COALESCE(SUM(LENGTH(CAST(payload AS BLOB))), 0) FROM listings WHERE ${mine}))`)
        .bind(key),
      this.db.prepare(`DELETE FROM listings WHERE ${mine}`).bind(key),
    ];
  }

  async purge(keyValue: unknown): Promise<number> {
    const results = await this.db.batch(this.purgeStatements(parseClient(keyValue)));
    return results[results.length - 1].meta.changes;
  }

  // A banned seller's sales are still answered and paid, so the ban isn't announced to them. They just never reach the market.
  async ban(keyValue: unknown): Promise<number> {
    const key = parseClient(keyValue);
    const results = await this.db.batch([
      this.db.prepare("INSERT OR REPLACE INTO banned_sellers (key, created_at) VALUES (?, ?)").bind(key, this.clock()),
      ...this.purgeStatements(key),
    ]);
    return results[results.length - 1].meta.changes;
  }

  async unban(keyValue: unknown): Promise<void> {
    await this.db.batch([this.db.prepare("DELETE FROM banned_sellers WHERE key = ?").bind(parseClient(keyValue))]);
  }
}

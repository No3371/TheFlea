-- item_value is the seller's value for one unit, sent with the sale. Minted offers copy it.
-- types is a JSON array of the item's categories. Listing IDs are random, so the primary key is
-- also a random order: offers walk it from a random point. D1 counts every index entry as a written
-- row, so there are no other indexes, and cleanup and admin routes scan the table. payload goes last,
-- so reading the other columns while walking doesn't follow a large payload's overflow pages.
CREATE TABLE listings (
    market TEXT NOT NULL,
    id TEXT NOT NULL,
    seller TEXT NOT NULL,
    source TEXT NOT NULL,
    item_id TEXT NOT NULL,
    units INTEGER NOT NULL,
    item_value REAL NOT NULL,
    types TEXT NOT NULL,
    created_at REAL NOT NULL,
    expires_at REAL NOT NULL,
    payload TEXT NOT NULL,
    PRIMARY KEY (market, id)
) WITHOUT ROWID;

-- One row for each offers request that sent real listings: what a later purchase report needs,
-- without the payloads. listings is a JSON object from listing ID to [item_id, units, types, created_at],
-- where created_at is the listing's own, so a purchase cancels what's left of its sale, and units are
-- its counted units. A reported listing leaves the object, and the row goes with its last one.
-- A row lasts as long as the longest listing and then goes, uncounted.
CREATE TABLE receipts (
    client TEXT NOT NULL,
    visit TEXT NOT NULL,
    market TEXT NOT NULL,
    sent_at REAL NOT NULL,
    listings TEXT NOT NULL,
    PRIMARY KEY (client, visit)
) WITHOUT ROWID;

CREATE TABLE trades (
    market TEXT NOT NULL,
    item_id TEXT NOT NULL,
    net REAL NOT NULL,
    volume REAL NOT NULL,
    updated_at REAL NOT NULL,
    PRIMARY KEY (market, item_id)
) WITHOUT ROWID;

CREATE TABLE category_trades (
    market TEXT NOT NULL,
    type TEXT NOT NULL,
    net REAL NOT NULL,
    volume REAL NOT NULL,
    updated_at REAL NOT NULL,
    PRIMARY KEY (market, type)
) WITHOUT ROWID;

CREATE TABLE batches (
    market TEXT NOT NULL,
    client TEXT NOT NULL,
    batch_id TEXT NOT NULL,
    response TEXT NOT NULL,
    created_at REAL NOT NULL,
    PRIMARY KEY (market, client, batch_id)
) WITHOUT ROWID;
CREATE INDEX batches_age ON batches (created_at);

-- live estimates the key's live listings, which set how long its new ones last. The daily cleanup
-- counts them, and each sale adds what it lists, so listings sent to buyers since still count.
CREATE TABLE seller_quota (
    key TEXT NOT NULL,
    day INTEGER NOT NULL,
    listings INTEGER NOT NULL,
    units INTEGER NOT NULL,
    live INTEGER NOT NULL,
    PRIMARY KEY (key, day)
) WITHOUT ROWID;

CREATE TABLE banned_sellers (
    key TEXT PRIMARY KEY,
    created_at REAL NOT NULL
) WITHOUT ROWID;

CREATE TABLE storage (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    listing_bytes INTEGER NOT NULL
);
INSERT INTO storage (id, listing_bytes) VALUES (1, 0);

# How The Flea works

The design of the mod and its market server, for people changing either. Players should start with the [README](../README.md).

- **Mod source:** `src/`, built and installed to `Mods/TheFlea/` by `dotnet build -c Release`
- **Server:** `worker/`, a Cloudflare Worker with a D1 database. See [worker/README.md](../worker/README.md) to run or deploy it.
- **Checks:** [TESTING.md](TESTING.md)
- **Trader levels:** set in `src/TraderLevel.cs`. A modified client can fake its level, but the server's limits still apply.
- **Market codes:** a sold batch settles in the market it was sold to, even if the code changes before the next visit. New offers always come from the current code.

## The visit, step by step

1. **Call 3532** and pick a category. The call is stored in the save, and the trader comes the next day, or sometimes in 2 or 3 days (60%, 30% and 10%). The trader says which on the phone. The roll depends only on your install and the day, so reloading and calling again gets the same answer.
2. **Settle:** when the next day's shutter opens, the mod POSTs the pending batch to `/v1/sales` and gets back a price multiplier per item.
3. **Offers:** if the shop is still open, the mod POSTs to `/v1/offers`, which picks random listings in that category and takes them off the market for good. The same request reports the real listings you bought on earlier visits. Minted offers fill the remaining slots, and if the server still sends fewer than the trader brings, the game mints the rest itself.
4. **Trader joins the queue:** the trader goes behind that day's customers, without removing any. On arrival, the trader pays `local value x sell multiplier` for each item in the batch, then lays out the offers it can spawn, priced at `item value x buy multiplier`.
5. **Selling:** a Harmony prefix on `PlayerStore.SellItem` stores the encoded item in the pending batch instead of paying cash.
6. **Buying:** a real listing you buy is noted in the save and reported with the next offers request. Offers you leave are gone, and the trader makes no request when he leaves.

The pending batch lives in the save, so a rollback restores the sold items and their unpaid batch together. Resending a batch ID returns the server's first answer and isn't counted twice. The list of unreported purchases is in the save too, so a rollback undoes a purchase and its record together.

Each answered offers request also stores the market's category prices and fee in `market_rates.json`, next to `client_id.txt`. The mod uses them to pay a batch when the server can't price it, as described under [Refused requests](#refused-requests) and [When the server is down](#when-the-server-is-down). A stored price fades toward 1x the way the server's prices do, losing half its effect every 2 real hours: `e^(ln(price) x 0.5^(hours / 2))`. The fee doesn't fade. Only the last market answered is stored, and its prices are used only for that market code.

The buy multiplier is `e^(item pressure + category pressure + drift)`, held between 0.5 and 1.5, and the trader takes a 10% cut on both sides: sellers receive the multiplier minus 10%, and buyers pay it plus 10% of the item's value. Offers show a `Flea market price` line and a `Trader's cut` line. Each pressure is `net / scale`:

- `net` goes down by each unit sold and fades by half every 2 real-time hours, so something with no recent activity adds nothing.
- A purchase first cancels whatever is left of that listing's sale, because that supply is gone, then adds 0.25 per unit as new demand. So a market where about 80% of listings sell quickly sits near 1x.
- `scale` is how many units usually trade within one price half-life, measured from a volume that fades by half every 12 hours, and never less than 20 for an item or 300 for a category. Busy items and categories take more units to move.
- Category pressure is the average across the item's categories, so an item in several categories isn't pushed further than an item in one.

Selling 60 bread with no other activity takes bread to the 0.5x floor and cheese, another food, to `e^(-60/300)`, about 0.82x. Half of that effect is gone after 2 hours.

Drift keeps a quiet market from sitting at 1x. Each major category gets a new random target every real hour, between -0.22 and 0.22, and its drift glides from one hour's target to the next. That's about 0.80x to 1.25x on its own, enough that a hot category can beat a normal customer even after the trader's cut: 1.25 x 0.9 is about 1.12x. An item takes the average drift of its major categories, so minor ones don't dilute it. Nothing is stored. Each target is an HMAC of the market, category and hour keyed with the `DRIFT_SALT` secret, so everyone in a market sees the same rates and nobody without the secret can predict the next hour. Without the secret there's no drift. The mod's cached rates include the drift of the hour they were fetched, and fade toward 1x as usual.

All of these are set in `DEFAULT_CONFIG` in `worker/src/market.ts`.

## Market modifiers on the tablet

With the trader at the counter, selecting an item you're consigning shows these lines under **Market and Merchant**, and the price on its row follows them:

- `Category price`: the current modifier of the item's major categories, averaged if it has several. This line is left out at 0%.
- `Trader's cut`: the 10% fee.
- `Type price (?)`: the item's own modifier, which isn't known yet.

So the row's price is a rough estimate of the payout. The recorded value leaves these lines out, and the payout is set on the next visit, when prices may have moved. The numbers arrive with the trader's offers, so they need no extra request, and they aren't available before the trader comes. When he comes without the server, they're the stored prices described under [When the server is down](#when-the-server-is-down).

## The game's price modifiers

While the trader is at the counter, active events and retail markup don't apply, in either direction. This stops them adding up with the market price to extremes like 10% or 300%. Perks such as perfume, addiction and Awful Negotiator still apply and keep their own lines, and so do an item's own qualities such as defects.

## Minted offers

Minted offers fill the slots real listings leave in a visit. Each one copies the value and categories of a random listing in that market, including the ones the same visit takes, so they follow what players sell. They aren't stored anywhere, and buying one doesn't move prices: only purchases of real listings are counted.

- **Item choice:** the buyer's game picks a random item from the offer's category, within 25% of the sold item's value. If none is that close, it uses the closest item within 2x, or the trader leaves the offer out.
- **Skipped items:** `NOT_FOR_RESALE` items, access cards, machines, contraband and items that start out stolen.
- **Label:** the tooltip shows `Seller: Unknown`, and nobody gets paid for the sale.
- **Price:** the server doesn't know which item will be picked, so the offer's multiplier uses only its category's pressure.

Set `mintOffers` to `false` in `DEFAULT_CONFIG` to turn this off.

If the market has too few listings to fill a visit, such as when you're its only seller, your game makes up the difference itself. These items are picked at random from the visit's category, or from any category, priced at their local value times the market's category modifier, and labelled `Seller: Unknown`. The server never offered them, so they're left out of the purchase report and don't move prices. If the server can't be reached, or answers with any error other than HTTP 400, see [When the server is down](#when-the-server-is-down).

## Refused requests

The server answers HTTP 400 to a request it will never accept, so retrying it would strand the batch for good.

- **Sale refused:** the batch is paid locally, and `Player.log` has `Market refused the batch`. Each item pays `value x the average price of its own categories x (1 - fee)`, using 1x for an item with none of them priced, held between 0.25x and 2x. The prices and fee come from that visit's offers if they're from the batch's market, otherwise from the stored prices of that market. With neither, the batch is paid at 0.5x its value, the same as an item the server sends no price for. The server didn't record it, so its items aren't listed and don't move prices. The batch is cleared when the trader pays and is never sent again, so it can't be paid twice.
- **Offers refused:** the trader comes with only items your game mints, and `Player.log` has `Market refused the offers request`. The purchases that request reported are dropped, because the same report would be refused again.
- **Full batch:** the trader takes at most his level's item count, also when several items sit on the counter at once. The server refuses more than 20.
- **Client ID:** `client_id.txt` must hold 32 lowercase hex characters. Anything else is replaced with a new ID, which also changes the day rolls.

## When the server is down

If the server can't be reached, or answers either request with any error other than HTTP 400, such as 404, 429 or 5xx, `Player.log` has `Market request failed`.

- **First failed day:** the trader doesn't come, because his visit also settles your last batch. He tries again the next day, and nothing is lost.
- **Second failed day in a row, and later:** the trader comes anyway with only items your game mints. A batch the server didn't price is paid the same way as a refused sale, from the stored prices of its market or at 0.5x without them. The minted offers and the tablet's consign lines use the current market's stored prices and fee, or 1x and no fee without them. `Player.log` has `the trader comes with local items and cached rates`.
- **Counting:** failed days are counted in the save, at most once per game day, and the count starts over on a day the server answers every request, even with HTTP 400. Purchases not yet reported wait for the next answered offers request.

A batch paid this way is cleared when the trader pays and is never sent to the server, so it can't be paid twice. If the server did record it before the connection broke, its items are still listed for other players.

## Stand-ins for missing items

If a listing needs an item this game doesn't have, such as one from a mod you don't use, your game replaces it with a local item the same way as a minted offer, labelled `Seller: Unknown`. Buying one uses up the listing, and counts as a purchase of the original item, the same as any real listing. The server took the listing off the market when it sent it, so leaving the stand-in, or a listing with no local item close enough in value, means nobody gets the original.

## Fake items

The trader checks each item you hand over, using the same rule the game uses for customers. It catches 90% of items that are worth less than they appear, says so, and the item's price drops before you sell it. You lose no reputation. The other 10% slip through and are paid as if real. The roll is fixed per item, so reloading or re-placing an item doesn't change it.

- **Cigarettes, stamps and injectors** keep their status. A caught one is listed as `(Counterfeit)`, and one that slipped through stays a hidden fake that the buyer can find by inspecting it.
- **Other faked items**, like false durability or purity, arrive showing their real values, even if the seller was overpaid.
- **Names and descriptions** are kept only if they contain the buyer's local text and add at most 32 characters. Otherwise the buyer's local text is used. Fish, certificates, stamps, counterfeits and finished wine get theirs from the buyer's game.
- **Sprites** are kept only if they're on the same sprite sheet as the item's default, exist in the buyer's game and are the same size as the default. Otherwise the item shows its default sprite.

## Limits on sellers

The game runs locally, so the server can't tell an earned item from a cheated one. These limits stop one person from dominating the market or harming buyers. All the numbers are in `DEFAULT_CONFIG`.

- **Hot goods and contraband:** the trader refuses contraband and stolen goods that still have heat, including inside containers. Stolen goods become sellable once they've cooled.
- **Worthless items:** the trader refuses them, and the server ignores any item without a value above 0. A buyer's game also rejects a listing worth nothing when it arrives.
- **Values:** the server doesn't list an item worth more than 100,000 a unit. Nothing in the game comes close.
- **Categories:** only the game's own categories (`TypeHelper.ALL_TYPES_LIST`) are kept, at most 5 per item, with the 11 major ones first. Other names are dropped.
- **Items not for sale:** the trader refuses machines, `NOT_FOR_RESALE` items and access cards. A buyer's game checks these and contraband against its own item definitions, including items inside containers, and also rejects hidden items and characters. A rejected listing becomes a local stand-in, the same as a missing item.
- **Values and stacks on arrival:** an item's value is held between 10% and 10x of the local default, and the secondary value fields are limited too. A top-level stack can't exceed the item's own limit. An item inside another one, with no limit of its own, can carry up to 999, because it may sit in something that holds more, like rounds in a magazine. Item ids in the payload are renumbered and its saved class name is replaced with the local one.
- **Listing lifetime:** a listing waits for a buyer for `3 + 11 x (1 - min(1, n / 100)^2)` days, where `n` is how many live listings its client ID already holds across all market codes: 14 days at none, 11.25 days at 50 and 3 days at 100 or more. The IP address gets the same curve against 200, and the shorter of the two lifetimes wins. Nothing is refused for holding too many, so a seller who lists a lot just has each listing go sooner. Sellers are paid either way. The daily cleanup counts each seller's live listings, and each sale adds what it lists, so a listing sent to a buyer still counts until the next cleanup.
- **Daily limits:** each client ID can send 200 listings a UTC day, and each IP address 400. Past that, items are still paid but aren't listed, because every listing written costs database writes. Past 300 units a day, or 600 per address, items are listed but don't move prices. The mod doesn't tell the player when either happens.
- **Only real listings move prices:** an item that can't be listed gets a price and changes nothing else. A stack over 999 counts as 1 unit.
- **Real offers:** each visit gives 25–50% of its slots to real listings, picked at random, so 2–4 of 8. Minted offers fill the rest, and real listings only go past their share if there aren't enough listings to copy.
- **Seller share:** one IP address can take at most half of a visit's real slots, rounded up. `ALLOW_SELF` turns this and the real offer share off, for testing alone.

Batches are remembered for 7 days, so a resent batch gets the same answer. Players resend only when a visit broke off. Past 20,000 remembered batches, the daily cleanup forgets the oldest, and an empty batch isn't remembered at all.

IP addresses are stored only as salted hashes. An IPv6 address counts by its /64 network, since one home connection usually gets a whole /64.

## Known limits

- **Item features aren't transferred:** defects and similar features are stored outside the item data. Some are rebuilt from tags on load, and the rest are lost. Cigarette, stamp and injector authenticity is the exception.
- **Names and looks can reset:** a modded item that renames itself falls back to its default name unless the new name contains it. An item drawn at a different size from its default shows its default sprite.
- **Forged labels:** a modified client can still add up to 32 characters to a real name or description, use any same-size sprite from the item's sheet, skip the trader's check, or omit an authenticity mark.
- **Missed fakes:** the check runs on the seller's game and has no penalty. Splitting a stack gives the new item its own roll.
- **Shapes reset to default:** to block forged shapes, incoming items use the local default shape.
- **Modded listings can be replaced:** a stand-in is a different item, and the listing is used up once it's offered, bought or not, so players with that mod lose the chance to buy it. A gun with one modded attachment becomes a different item entirely, not the same gun without the attachment.
- **Forged data:** values are held between 10% and 10x of the local default, but other tags and item types still come from the seller. The saved class name (`itemType`) is the exception, taken from the buyer's game. A forged tag such as `ITEM_BEHIND_LOCK_TAG` can leave an item in a container out of the price.
- **Nested stacks:** only the top-level item's stack is held to its own limit. An item inside a container, with no stack rule of its own, can arrive with up to 999 units, because the mod can't tell which holders allow more than one.
- **Batches paid without the server:** a batch the server refuses with HTTP 400, or one paid after two failed days, is priced from category prices only, which may be stale, and at 0.5x when no prices are stored. Its items aren't listed. The stored prices belong to the install, so they're shared by every save and only the last market answered is kept.
- **Cheated items:** an item spawned with a cheat is identical to an earned one, so it can still be sold within the limits. A modified client can also sell hot goods, because the buyer's game can't see heat.
- **Expired listings' prices:** a listing that expires unsold doesn't undo its earlier effect on prices. It fades as usual.
- **Live counts are estimates:** a seller's live listings are counted once a day by the cleanup, so listings sent to buyers since still shorten that seller's new ones until the next cleanup. If the cleanup misses a whole day, a seller who didn't sell that day starts again from 0.
- **Rare categories:** a category request looks at no more than 500 listings, starting from a random point, so in a large market it often misses a rare category's listings, and minted offers fill their slots instead.
- **Resale profit:** a listing valued at 10x and bought at the 0.5x floor costs 5x and is then worth 10x. Prices are floored per item, so reaching this takes a low price on that item, which one seller's quota can still cause.
- **Low-value items can cost more:** a real item worth under 10% of its default arrives at 10% of its default, for both the buyer's price and its later value.
- **Seller names:** each listing stores the seller's current Steam name, which the server and other players can read, unless **Sell anonymously** is on. **Hide other sellers' names** shows every seller as `Flea Market` on your side only. Names aren't verified or moderated, so a modified client can send any name. They are capped at 24 characters with markup removed.
- **Forged mint values:** a modified client can report any value, which changes which item gets minted. The buyer still pays that item's price, so this can't create free value, but it can put expensive items on the market.
- **Minted item prices:** a minted offer ignores the picked item's own pressure, so it can be cheaper or dearer than a real listing of the same item. Buying it doesn't raise demand either, so prices only rise through real listings.
- **Event resale:** events only stop applying while the trader is present. A bought item resold to a normal customer during an event gets the event's price.
- **Forged purchases:** the server trusts the mod's report of what was bought, so a modified client could raise a price without paying for anything. It can only report listings it was sent, each once and within 14 days, so raising a price this way also takes those listings off the market. Purchases are reported on the next visit, so prices react a visit late, and a purchase is never reported if you stop calling the trader.
- **No accounts:** the client ID is a random token, so quotas, the seller share and bans can be avoided by using several IP addresses, and players sharing an address share its limits. Rate limiting is per IP address, about 10 requests a minute plus 3 sales a minute. It's only approximate: Cloudflare counts locally and syncs in the background, and in one live test, blocking started after about 40 requests. Each Cloudflare location also counts separately, and using several addresses avoids it.
- **Open storage:** anyone with the URL can post listings, and the rate limit and daily quotas hold them back. Each listing can be up to 64 KB and lasts up to 14 days, less for a seller holding many. When stored listings reach 300 MB, sales are still counted and paid but nothing new is listed until old listings go, so someone with enough IP addresses could stop new listings for every market, or use up the account's daily limits. Asking for offers writes rows too, 1 for each listing sent plus 2.
- **Offers use up listings:** every offers request takes its real listings off the market, bought or not, so anyone can request offers repeatedly, at the rate limit, to clear listings. They're gone for good rather than held for a while, and only a small receipt for each visit is kept, for 14 days. Each visit's receipt also costs reads on every later purchase report from that player until it's fully reported or expires. This isn't limited per seller. The mod asks only on a day the trader can come and the shop is open, but a visit the shop closes on after the request still uses them up.
- **CPU limit:** parsing a batch of 20 items at 5 KB took about 1 ms in a local test, but 20 items at 60 KB took about 10 ms, right at the free plan's limit. If real items turn out that large, a batch could fail every time and stay unpaid. Check real item sizes before relying on this.
- **If the mod breaks:** the game saves only at the start of a day, and customer queues aren't saved, so a due trader returns when the shutter opens. Mod data stays in the save. If a game update breaks a required patch, the loader turns the whole mod off: the contact shows the game's fallback dialogue and any unpaid batch waits in the save until the mod works again.
- **Uninstalling:** the contact stays in the save, and calling it then shows the game's fallback dialogue.
- **Mod compatibility:** another mod that patches `PlayerStore.SellItem` can conflict with this one.
- **Language:** English, Chinese and Japanese text are written. The game's only Chinese locale is `zh`, Simplified, so that's what `zh.csv` holds.

## Translating

Add `Localization/<locale>.csv` using the game's locale code, like `fr.csv`, and set `lang_code` in it to the same code. The mod uses the file for the player's language and falls back to `en.csv` for any missing line. Category names come from the game, so they're already translated.

Numbered keys like `visit_offers_1` and `visit_offers_2` are variations of one line, picked at random without repeating the last one. A translation can have any number of them, or a single `visit_offers` line. Keep the `{0}` placeholders in the payment lines.

# In-game checks

The Worker is covered by its own tests (`npm test` in `worker/`). The full in-game flow has to be checked by hand, and hasn't been checked end to end yet.

Check the F8 console for the mod's messages and errors after each step.

## Automated checks

Run these first. They don't replace the checks below, which test the whole flow in the game.

**Rules without the game**: `dotnet test tests`. These tests cover the code in `src/Core`, which both the mod and the tests compile:
- the arrival day's spread and its fixed roll
- failed-day counting
- fallback pricing: stored and faded rates, the flat 0.5x, the fee and the clamps
- reading `market_rates.json`, including a damaged file
- `client_id.txt` checks
- the trader's level thresholds
- the payout line's numbers
- cleaning market codes and seller names
- the name checks
- picking minted items by price

Test names follow the checks below.

**In the game**: load a test save, open F9 → **Modding** → **The Flea: run self-tests**. Results go to the F8 console and `Player.log` with the prefix `[Self-test]`, ending with a line that counts passes, failures and skips. It checks:
- selling and buying back every item the trader would take, comparing count, value, sprite, shape, contents and state
- that every machine, not-for-resale item, access card and contraband item is refused
- the same round trip for items it builds itself:
  - finished and counterfeit wine
  - part-filled liquid containers
  - part-charged batteries
  - stacks of 7
  - guns with every slot fitted
  - loaded magazines
  - containers holding stacks
- the same round trip for anything you put on the counter, as it is in your game. This is optional.
- forged listings: a different item than announced, an item this game doesn't have, a blocked item, a character, a wrong `itemType`, huge or negative ids, a 999 stack, a huge value, a name with markup or in another language, sprites from another sheet or that don't exist, a seller name with markup, loops and shared or missing children, and payloads that are empty or not JSON
- every valid fake mark on cigarettes, stamps and injectors survives a round trip, a caught fake arrives exposed, and made-up or misplaced marks are ignored
- minted offers cost within 25% of the sold item, can be resold and show `Seller: Unknown`, and local offers come from the category asked for

Decoding creates items and uses up unique IDs, so run the self-tests on a save you don't need.

**Phone and arrival**

- [x] The first time a run opens the shutter with the mod, the Flea's card lies on the front counter like the newspaper, with its sprite, name and 3532 in the tooltip. A run started before the mod was installed gets it on its next morning.
- [x] Using the card shows the large card art, sharp and not blurred, and using it again puts it away.
- [x] The card comes once per run: not again the next day, after a reload, or after selling or trashing it.
- [x] The trader refuses the card.
- [x] 3532 appears in the phone book and answers.
- [x] Six choices fit on the phone screen, and each one selects the choice it shows.
- [x] All category pages display, and **What else?** and **Never mind.** work.
- [x] Other phone contacts still show their choices in the usual places.
- [x] The trader doesn't come the day you call, even if you call before opening the shutter.
- [x] The day the trader named on the phone, the trader comes after that day's usual customers, and none of them are missing. Most calls say tomorrow, some say 2 or 3 days.
- [ ] Reload and call again on the same day: the trader names the same day.
- [x] Close the shop before the trader arrives: the trader comes the following day instead.
- [x] Call, sleep, then reload: the trader still comes once you open the shutter. The queue is empty until then, for every customer.
- [x] Call again while a trader is already due: the trader still comes on the original day, with the new order.
- [x] The trader comes at all on his day. The mod checks that the shop is open when the day's customers are generated, before asking for offers, so if he never comes and nothing is logged, that check is the likely cause.
- [ ] With the shop closed before the offers request goes out, no offers are requested, and the trader comes the following day with the batch still unpaid.
- [ ] With the server stopped, no trader comes after about 6 seconds of retries, `Player.log` has `Market request failed`, nothing is lost, and the trader comes the next day the server is up.
- [ ] With the server stopped on two days in a row, the trader comes on the second day with only minted items, pays the batch from the stored prices, and `Player.log` has `the trader comes with local items and cached rates`. The tablet's consign lines show the stored prices. The next visit doesn't pay the batch again, and the server never receives it.
- [ ] Do the same with no `market_rates.json`: the batch is paid at 0.5x, minted items cost their plain value, and the consign lines show no category price and no cut.
- [ ] After a fallback visit, start the server: the next visit is normal, and later failures again wait one day before the trader comes without the server.
- [ ] If the market requests fail more than once on the first failed day, each failure logs `trying again tomorrow`, and the trader doesn't come that day.
- [x] With `/v1/sales` answering 400, such as a hand-edited batch of 21 items in the save, the trader comes anyway, pays the batch from that visit's category prices less the cut, and `Player.log` has `Market refused the batch`. The next visit doesn't pay it again.
- [ ] With `/v1/sales` and `/v1/offers` both answering 400, the batch is paid from `market_rates.json`, faded by the time since it was written.
- [ ] With `/v1/offers` answering 400, the trader comes with a full set of minted items, and `Player.log` has `Market refused the offers request`.
- [ ] After an answered offers request, `market_rates.json` sits next to `client_id.txt` with the market code, the category prices, the fee and the time. A damaged file is ignored and replaced on the next answered request.
- [ ] Put `not-an-id` in `client_id.txt` and restart: the file gets a new 32-character ID and the trader comes as usual.

**Selling and payout**

- [x] Selling an item removes it, pays no cash, and doesn't add evidence or reputation.
- [x] The trader refuses machines, `NOT_FOR_RESALE` items, access cards and items worth nothing.
- [x] Contraband and hot stolen goods get their own refusal lines, and so does a container holding either. No reputation or evidence changes.
- [ ] A stolen item is accepted after enough nights for its heat to reach 0.
- [x] A new save gets 4 offers and 4 items a visit. The fifth item gets the "full" line.
- [x] With 3 items consigned, place 3 more on the counter at once and sell them: the fourth is taken, the others get the "full" line and stay on the counter, and no cash or reputation changes.
- [ ] Reaching 2,500 credits through a payout adds a level-up line after the payment line. Reaching it through a purchase makes the trader say it at the counter, and the next item is accepted straight away.
- [x] Get paid, then quit without sleeping and reload: the credits and level go back with the payout.
- [ ] An item that defects bring down to nothing is refused when you try to sell it, and stays on the counter.
- [ ] On the next visit you get the expected payout, once only.
- [x] The payment line shows the payout and how far it is over or under the batch's value, and the numbers add up.
- [x] Sell, sleep, then quit before the next call: the payout still arrives after reloading.
- [ ] Sell, then quit without sleeping: the items are back after reloading and the batch is gone.
- [ ] Get paid, then quit without sleeping and reload: the batch pays again with the same total, and `sales` in the database doesn't grow.

**Market codes**

- [x] **Market code** shows `0` in the settings, and a changed code survives a restart.
- [ ] An item sold under one code isn't offered under another, and comes back when you switch to the first code.
- [ ] Sell under one code, change the code, then get paid: the payout arrives, and the item is listed under the first code.

**Tablet market lines**

- [x] Selecting an item you're consigning shows `Category price`, `Trader's cut` and `Type price (?)` under **Market and Merchant**.
- [x] The row's price follows those lines, and `Category price` is missing when its category is at 0%.
- [x] The next payout's value leaves those lines out, so they aren't counted twice.
- [x] Taking an item off the counter, or the trader leaving with one on it, removes the lines. Normal customers never see them.

**Trader prices.** Test these during an active event that affects the item:

- [x] An offer shows no event line, and its price matches the `Flea market price` and `Trader's cut +10%` lines together.
- [x] Dragging an offer around the counter doesn't bring the event line back.
- [x] An item you consign shows no event or retail markup line, and the next payout isn't inflated by the event.
- [x] A container with items inside is priced without the event, and the items inside follow the market price.
- [ ] Perfume used on the trader still changes prices and shows its own line. So does Awful Negotiator, if you have it.
- [ ] After buying, the item gets event prices again when you offer it to a normal customer.
- [ ] Normal customers' prices are unchanged while the trader isn't at the counter.

**Minted offers**

- [x] Opening the shutter soon after a game loads causes no lag wave.
- [x] With too few real listings, the trader fills the empty slots with items from the category you asked for.
- [x] Minted items cost about the same as the item that was sold, and their tooltip shows `Seller: Unknown`.
- [x] No quest items, machines, contraband or items that can't be resold appear.
- [x] Bought minted items survive a save and reload, and can be sold back to the trader.
- [x] Your own sales don't come back to you as minted offers, unless `ALLOW_SELF` is on.
- [x] As the market's only seller, a visit still brings the full number of offers, all minted by your game.
- [x] Buying a minted offer leaves the market's prices unchanged.

**Purchase reports**

- [ ] Buying a real listing changes nothing on the server until the next visit. That visit's offers request carries its listing id in `bought`, and the item's price goes up afterwards.
- [ ] Buying minted offers and local items adds nothing to `bought`.
- [ ] After that request is answered, the list is gone from the save, and the visit after that sends an empty `bought`.
- [ ] Buy a real listing, then quit without sleeping and reload: the purchase and its record are both gone, and the next request doesn't report it.
- [ ] With the server down on the next visit, the purchase stays in the save and is reported on the first visit the server answers.
- [ ] Offers you leave aren't offered again to you or anyone else, and the trader makes no request when he leaves.

**Rich items, the riskiest part.** Sell and buy back each of these:

- [x] Wine bottle: state, sprite and value survive.
- [x] Gun with attachments: attachments survive, and the shape is correct.
- [x] Filled water bottle: liquid level and sprite survive.
- [x] Battery: charge survives.
- [x] Container with items inside.
- [x] A bought item survives a save and reload.
- [x] An offer not at 1x lists `Flea market price` with its percentage among the item's features, and the asking price matches it.
- [x] Dragging an offer around the counter keeps that line and price.
- [ ] After buying, that line is gone and the item is back to its full value.
- [x] A bought item's tooltip shows `Seller: [Steam name]`, or `Seller: Anonymous` for items sold with **Sell anonymously** on. The line survives a save and reload.
- [ ] With **Hide other sellers' names** on, every seller line shows `Seller: Flea Market`, and turning it off brings the names back.
- [ ] Real listings arrive with their seller's name, not `Seller: Unknown`, so the item check on arrival accepts them.
- [ ] A bought stackable item doesn't lose or wrongly take a seller name when stacked with other items.
- [ ] Reselling a bought item shows your name to the next buyer, not the first seller's.
- [ ] With a mod disabled, a listing from that mod becomes a similar-value local item labelled `Seller: Unknown`, and no "unknown" item appears.
- [ ] A stack of steel sheets, coins or exotic fuel keeps its count, and a battery, water bottle and container keep their value.
- [ ] A low-value item such as a fish or a used-up item arrives at a sensible price, and isn't turned into a stand-in.
- [x] Hand-edited listings become stand-ins if the item is a machine or worth nothing, and a forged 999 stack of a single item arrives as 1 when it's the top-level item.
- [x] A listing with negative or huge item ids, or a wrong `itemType`, arrives as the right item with its contents, or as a stand-in, and doesn't disturb anything else.
- [x] A container with stacks inside keeps their counts. (Magazines are gun attachments and hold no rounds.)
- [ ] Buying that stand-in reports the original listing as bought on the next visit. Leaving it means nobody gets the original.

**Fake items**

- [ ] Placing a fake cigarette, stamp or injector makes the trader speak up, drops its price and costs no reputation.
- [ ] Removing and re-placing an item, or reloading the morning save, gives the same result.
- [ ] A caught item arrives for the buyer named `(Counterfeit)`, at the lower price, including in another language.
- [ ] An item that slipped through arrives looking genuine, and inspecting it can expose it.
- [ ] Items in a container are checked too, and selling works after a catch.
- [x] A listing with a changed name or description shows the buyer's local one, while `Large` and `(Counterfeit)` names survive.
- [x] A listing whose sprite is from another sheet, or doesn't exist in the buyer's game, shows the default sprite with no dummy image.
- [ ] A bought fish, finished wine and grown produce keep their names and looks.

If real listings all turn into `Seller: Unknown` items, decoding is failing, and the most likely cause is the save bag wrapper in `ItemCodec.DecodeThroughBag`. If they don't appear at all, look for `Skipped offer` in the console.

![](https://github.com/No3371/TheFlea/blob/master/arts/banner.png)

# The Flea

A mod for Probably Stolen.

I've always been fascinated by "passive multiplayer". I even built one such mod for The Long Dark. For Probably Stolen, a player trading mod is the most straight forward direction, it's nothing new and people have done it, but I still want to try building one that is balanced, integrated and less-immersion-breaking.

The Flea is a trader you call on the phone. He buys the items you no longer want and brings items other players sold. Prices rise and fall with what players are actually selling and buying.

This mod changes the game's economy and talks to an online market server. See [What the mod sends](#what-the-mod-sends) before you turn it on.

## Getting started

1. Open the Mods menu, select **The Flea** and press **Activate**.
2. Approve the network and Steam prompt, then restart the game.
3. His number, **3532**, is now in your phone book.

## How to play

1. **Call 3532** and tell him what kind of items you want. He says when he'll come: usually tomorrow, sometimes in 2 or 3 days.
2. **Open your shop that day.** He joins the queue behind your usual customers.
3. **Buy from him.** He lays out his offers on the counter. Most were sold by other players, and the rest are items of his own.
4. **Sell to him.** Hand him items the way you would any customer. He takes them on consignment, so you get no cash yet.
5. **Call him again to get paid.** On his next visit he pays for everything you gave him last time.

If you close the shop before he arrives, he comes the next day you open.

## Prices

- **Every deal has a market price,** between 50% and 150% of the item's normal value.
- **Selling pushes a price down, buying pushes it up.** When many players sell the same kind of item, it gets cheaper.
- **Prices drift back to normal** within a few hours.
- **He takes a 10% cut** on both sides: you pay the market price plus 10%, and you're paid the market price minus 10%.
- **You're paid at the price on the day he pays,** not the day you handed the items over. The tablet shows an estimate.

Events and your retail markup don't apply to his deals. Perks such as perfume still do.

## Trader levels

He brings more and takes more as you trade with him. Every credit that changes hands between you counts.

| Level | Credits traded | Offers he brings, and items he takes |
|---|---|---|
| 1 | 0 | 4 |
| 2 | 2,500 | 6 |
| 3 | 7,500 | 8 |
| 4 | 20,000 | 10 |
| 5 | 50,000 | 12 |

## What he won't take

- Contraband, or stolen goods that are still hot.
- Machines, access cards and items marked not for resale.
- Items worth nothing.

Being refused costs you nothing.

## Fakes

He inspects what you hand him and catches 9 fakes out of 10. A caught fake is paid at its real, lower price. One that slips through is paid as if genuine.

It works both ways: a fake that got past him arrives at another player's shop still looking genuine. Inspect what you buy.

## Items you buy

- **`Seller:`** shows the Steam name of the player who sold the item, or `Anonymous`.
- **`Seller: Unknown`** marks an item of his own. You also get one of these in place of an item from a mod you don't have.
- **Most items keep their state,** such as a gun's attachments or a bottle's contents. Defects don't carry over.
- **Offers you leave are gone.** He doesn't bring them back.

## Settings

Press the gear button on the mod in the Mods menu.

| Setting | What it does |
|---|---|
| **Market code** | Players with the same code trade only with each other. `0` is the public market. |
| **Sell anonymously** | Hides your Steam name on items you sell. |
| **Hide other sellers' names** | Shows every seller as `Flea Market` on your screen. |
| **Days between calls** | How long before you can call him again. The default is 1. |
| **Market server URL** | Leave it alone unless you run your own server. |

To trade just with friends, set the same **Market code** on each game. A code is not a password: anyone who guesses it can join.

## If the market can't be reached

He skips a day and tries again, and nothing is lost. If it's still unreachable the next day, he comes anyway with only his own items, and pays what he owes you at the last prices he knew.

## What the mod sends

The mod contacts the market server only when you open the shop on a day he's due. The phone call sends nothing.

It sends:

- The items you consigned.
- Your Steam name, shown to whoever buys them. **Sell anonymously** turns this off.
- A random ID made for this install. It isn't your Steam ID.
- Your market code and the kind of items you asked for.
- Which second-hand items you bought on earlier visits.

Like any website, the server sees your IP address. It keeps only a scrambled form, used to limit how much one person can list.

Seller names aren't moderated. Use **Hide other sellers' names** if you'd rather not see them.

## Good to know

- **Your save holds what you're owed.** Reloading an earlier save brings back the items you handed over and cancels that deal.
- **You're always paid,** even when the market has too many of your items to show them all to other players.
- **Cheated items can't be told from earned ones.** Use a private market code if that bothers you.
- **If you uninstall,** the phone contact stays in your save, and anything you were owed stays unpaid.

## Languages

English, Simplified Chinese and Japanese are included.

## For developers

- [How it works](docs/DESIGN.md): the visit flow, the price formula, the server's limits, known weaknesses and how to add a language.
- [Market server](worker/README.md): running it locally, deploying your own, and admin routes.
- [In-game checks](docs/TESTING.md): the manual test list.

The mod source is in `src/` and builds with `dotnet build -c Release`.

# Market server

The Cloudflare Worker and D1 database behind The Flea. How the market behaves is described in [docs/DESIGN.md](../docs/DESIGN.md).

## Try it

From the `worker` folder, install the tools and create the local database:

```bash
npm ci
```

```bash
npx wrangler d1 migrations apply the-flea-market --local
```

Sales are refused until `IP_SALT` is set, so give the local Worker one in `worker/.dev.vars`, which stays out of Git. `DRIFT_SALT` turns on the hourly category drift:

```bash
printf 'IP_SALT=local-test\nDRIFT_SALT=local-drift\n' > .dev.vars
```

Start the Worker. `ALLOW_SELF` lets you buy your own listings, which you need when testing alone:

```bash
npx wrangler dev --var ALLOW_SELF:true
```

It listens on `http://127.0.0.1:8787`. Set **Market server URL** to that address in the mod's settings to use it.

Run the tests and the type check from the same folder:

```bash
npm test
```

```bash
npm run check
```

Then, in the game:

1. Create an empty `dev.txt` next to `Probably Stolen.exe` to enable the F8 console.
2. Open the Mods menu, select **The Flea**, press **Activate**, approve the network and Steam prompt, and restart.
3. In the F8 console, run `mod-scan the_flea`. It should report only `network` and `steam`.
4. Use a throwaway save.

To shorten the wait between calls, set **Days between calls** to 0 behind the gear button in the Mods menu.

## Deploy to Cloudflare

Everything here runs from the `worker` folder, and the free Workers plan covers it.

1. Install the tools and sign in:

   ```bash
   npm ci
   ```

   ```bash
   npx wrangler login
   ```

2. Deploy. On a new Cloudflare account, remove `database_id` from `wrangler.jsonc` first, so this creates your own D1 database:

   ```bash
   npx wrangler deploy
   ```

3. Create the tables:

   ```bash
   npx wrangler d1 migrations apply the-flea-market --remote
   ```

4. Leave `ALLOW_SELF` off: it lets players buy their own listings and turns off the seller share.

5. Set the secrets. Each command asks for a value, so use a long random string. `IP_SALT` hides IP addresses in the database, and sales are refused until it's set. Changing it restarts everyone's IP quota and seller share. `DRIFT_SALT` turns on the hourly category drift, and keeps it unpredictable. Changing it reshuffles the drift from then on. `ADMIN_TOKEN` turns on the admin routes:

   ```bash
   npx wrangler secret put IP_SALT
   ```

   ```bash
   npx wrangler secret put DRIFT_SALT
   ```

   ```bash
   npx wrangler secret put ADMIN_TOKEN
   ```

6. Check that `https://the-flea-market.<your-subdomain>.workers.dev/v1/health` returns `{"ok":true}`. This doesn't touch the database, so also try a test visit.
7. In the game, open the mod's settings and set **Market server URL** to that address, without `/v1/health`.

To update a Worker that's already live, apply any new migrations before deploying, because new code needs the new tables. A mod built before offers became `POST /v1/offers` can't get offers from the new Worker.

### Moving from probably-second-hand-market

The Worker and its database used to be called `probably-second-hand-market`. They're now `the-flea-market`, and the schema was rewritten into the single `0001_market.sql`, so the old database can't be migrated. Following the steps above creates a new Worker and a new, empty database under the new name, at a new address. The secrets in step 5 have to be set again, because they belong to the Worker.

The old Worker and database keep running until you delete them. Once the new Worker answers, remove them. This deletes every old listing and price:

```bash
npx wrangler delete --name probably-second-hand-market
```

```bash
npx wrangler d1 delete probably-second-hand-market
```

For a local database made with the earlier migrations, delete the `worker/.wrangler/state/v3/d1` folder, which holds only local data, and apply the migrations with `--local` again. That doesn't touch the Cloudflare database.

To follow errors on the live Worker, run `npx wrangler tail`.

### Free plan limits

The limits are per Cloudflare account. Going over one causes errors until the daily reset, not a bill, and the mod puts the trader's visit off until the next day.

| Limit | Amount | What it means here |
|---|---|---|
| Rows written | 100,000 a day | About 2,200 visits a day, the first limit you'd reach. D1 counts every index entry as a written row, so listings and receipts have no indexes besides their primary key. A visit with 8 sold in mixed categories, 8 offered and 4 bought wrote 41–45 rows in the tests: 31 for its sale and 10–14 for its offers request, or 49 when all 8 offers were real. A sale writes 1 row for each listing, 1 for each item and category price it moves (8 and 11 here), 2 to remember the batch and 1 each for the seller's quota and the storage total. An offers request writes 1 row for each real listing sent, plus 1 receipt for the whole visit and 1 storage total, and a report about 1 for each item and category bought and 1 for each receipt it changes. The daily cleanup also writes 1 row for each seller holding listings, and deletes what's expired. |
| Rows read | 5 million a day | About 17,000 visits a day. A visit reads about 270–370 rows at any market size: about 100 for its sale and 160–270 for its offers request. D1 also counts the entries of JSON a query walks through, so recording purchases reads each of the buyer's open receipts and their listings, about 40 rows for one, and more for a player with many unreported visits in the last 14 days. A request for a category looks at no more than 500 listings, so a rare category can cost up to 500 more. A sale that collides with another from the same seller reads its rows again, up to 3 times. |
| Requests | 100,000 a day | At most 2 a visit |
| Database size | 500 MB | The market stops taking listings at 300 MB, about 4,800 listings if all were the 64 KB maximum |
| CPU time | 10 ms a request | See [Known limits](../docs/DESIGN.md#known-limits) |

Check real usage under **D1 > your database > Metrics** in the Cloudflare dashboard.

## Admin

The admin routes don't exist until `ADMIN_TOKEN` is set. Send it as `Authorization: Bearer <token>`.

| Route | What it does |
|---|---|
| `GET /v1/admin/sellers?market=0` | Lists the 50 sellers holding the most listings, with their IP hash, client IDs, displayed names and use of today's quota |
| `POST /v1/admin/purge` with `{"key": "..."}` | Removes everything that client ID or IP hash has listed, in every market |
| `POST /v1/admin/ban` with `{"key": "..."}` | Does the same and keeps them out. Their sales are still answered and paid, but never reach the market |
| `POST /v1/admin/unban` with `{"key": "..."}` | Lifts a ban |

Neither a purge nor a ban touches listings already sent to a buyer. Those have left the market, and the buyer's purchase report still counts.

For example, to ban a seller:

```bash
curl -X POST "$MARKET_URL/v1/admin/ban" -H "Authorization: Bearer $ADMIN_TOKEN" -H "Content-Type: application/json" -d '{"key":"<client ID or IP hash>"}'
```

Every POST must be sent as `Content-Type: application/json`, or it's refused. Browsers can't send that from another site without asking first, so a web page can't post from its visitors' addresses. That includes asking for offers, which takes listings off the market.

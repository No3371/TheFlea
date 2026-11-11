// Keeps quiet markets moving. Each category gets a new random target every hour, and its price glides
// between targets, so a visit can find a category running hot or cold even when nobody has traded it.
// Nothing is stored: the targets come from a keyed hash of the market, category and hour, so everyone
// in a market sees the same rates, and without the secret nobody can work out the next hour's.

const SECONDS_PER_HOUR = 3600;
const UINT32_RANGE = 2 ** 32;

// A number in [-1, 1) fixed for this key, market, category and hour.
async function target(key: CryptoKey, market: string, category: string, hour: number): Promise<number> {
  const digest = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${market}|${category}|${hour}`));
  return (new DataView(digest).getUint32(0) / UINT32_RANGE) * 2 - 1;
}

// Smoothstep, so the price eases out of one target and into the next instead of turning on the hour.
function ease(fraction: number): number {
  return fraction * fraction * (3 - 2 * fraction);
}

// Each category's drift at `now`, in the same log units as trade pressure, between -amplitude and amplitude.
// Without a secret, or with no amplitude, there's no drift.
export async function categoryDrift(secret: string | undefined, amplitude: number, market: string, categories: string[], now: number): Promise<Map<string, number>> {
  const drift = new Map<string, number>();
  if (!secret || amplitude <= 0 || categories.length === 0) {
    return drift;
  }
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(`drift|${secret}`), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const hours = now / SECONDS_PER_HOUR;
  const hour = Math.floor(hours);
  const blend = ease(hours - hour);
  await Promise.all(
    [...new Set(categories)].map(async (category) => {
      const [from, to] = await Promise.all([target(key, market, category, hour), target(key, market, category, hour + 1)]);
      drift.set(category, amplitude * (from + (to - from) * blend));
    }),
  );
  return drift;
}

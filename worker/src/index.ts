import { BadRequest, DEFAULT_CONFIG, Market } from "./market.ts";

const MAX_BODY_BYTES = 4 * 1024 * 1024;
// A browser sends a cross-site POST without asking first only for a few types, all other than JSON.
// Requiring JSON makes it ask, and nothing answers, so a web page can't post from its visitors' addresses.
const JSON_TYPE = /^application\/json\s*(;\s*charset\s*=\s*"?[\w-]+"?\s*)?$/i;

interface Limiter {
  limit(options: { key: string }): Promise<{ success: boolean }>;
}

// Set with `wrangler secret put`, so they aren't in the generated Env. Admin routes don't exist without a token.
interface Secrets {
  ADMIN_TOKEN?: string;
  IP_SALT?: string;
  // Keys the hourly category drift. Without it, prices only move with trades.
  DRIFT_SALT?: string;
}

function send(status: number, body: unknown): Response {
  return Response.json(body, { status });
}

async function readBody(request: Request): Promise<Record<string, unknown>> {
  if (Number(request.headers.get("Content-Length") ?? 0) > MAX_BODY_BYTES) {
    throw new BadRequest("bad body size");
  }
  const text = await request.text();
  if (new TextEncoder().encode(text).length > MAX_BODY_BYTES) {
    throw new BadRequest("bad body size");
  }
  let body: unknown;
  try {
    body = JSON.parse(text);
  } catch {
    throw new BadRequest("bad json");
  }
  if (body === null || typeof body !== "object" || Array.isArray(body)) {
    throw new BadRequest("bad json");
  }
  return body as Record<string, unknown>;
}

// One IPv6 home connection usually gets a whole /64, so that's what counts as one address.
export function network(ip: string): string {
  if (!ip.includes(":")) {
    return ip;
  }
  const [head, tail = ""] = ip.toLowerCase().split("::");
  const front = head === "" ? [] : head.split(":");
  const back = tail === "" ? [] : tail.split(":");
  const groups = ip.includes("::") ? [...front, ...Array(Math.max(0, 8 - front.length - back.length)).fill("0"), ...back] : front;
  return `${groups.slice(0, 4).map((group) => group.replace(/^0+(?=.)/, "")).join(":")}::/64`;
}

function address(request: Request): string {
  const ip = request.headers.get("CF-Connecting-IP");
  return ip === null ? "unknown" : network(ip);
}

function hex(bytes: ArrayBuffer): string {
  return [...new Uint8Array(bytes)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function sha256(text: string): Promise<ArrayBuffer> {
  return crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
}

// Sellers are told apart by IP address without storing one. The salt stops anyone with the
// database from trying every address. Without the header, as in local runs, only the client ID counts.
async function sourceOf(request: Request, salt: string | undefined): Promise<string | undefined> {
  if (request.headers.get("CF-Connecting-IP") === null) {
    return undefined;
  }
  if (!salt) {
    throw new Error("IP_SALT is not set, so sales are refused rather than storing guessable IP hashes");
  }
  return hex(await sha256(`${salt}:${address(request)}`)).slice(0, 32);
}

// Compares digests, so the time taken says nothing about how much of the token was right.
async function isAdmin(request: Request, token: string): Promise<boolean> {
  const sent = hex(await sha256(request.headers.get("Authorization") ?? ""));
  const wanted = hex(await sha256(`Bearer ${token}`));
  let difference = 0;
  for (let index = 0; index < wanted.length; index++) {
    difference |= sent.charCodeAt(index) ^ wanted.charCodeAt(index);
  }
  return difference === 0;
}

async function routeAdmin(request: Request, url: URL, market: Market): Promise<Response> {
  switch (`${request.method} ${url.pathname}`) {
    case "GET /v1/admin/sellers":
      return send(200, { sellers: await market.sellers(url.searchParams.get("market")) });
    case "POST /v1/admin/purge":
      return send(200, { purged: await market.purge((await readBody(request)).key) });
    case "POST /v1/admin/ban":
      return send(200, { purged: await market.ban((await readBody(request)).key) });
    case "POST /v1/admin/unban":
      await market.unban((await readBody(request)).key);
      return send(200, { ok: true });
    default:
      return send(404, { error: "not found" });
  }
}

async function route(request: Request, market: Market, salesLimiter: Limiter, secrets: Secrets): Promise<Response> {
  const url = new URL(request.url);
  if (request.method === "POST" && !JSON_TYPE.test(request.headers.get("Content-Type") ?? "")) {
    throw new BadRequest("bad content type");
  }
  if (url.pathname.startsWith("/v1/admin/")) {
    if (!secrets.ADMIN_TOKEN) {
      return send(404, { error: "not found" });
    }
    return (await isAdmin(request, secrets.ADMIN_TOKEN)) ? routeAdmin(request, url, market) : send(401, { error: "unauthorized" });
  }
  switch (`${request.method} ${url.pathname}`) {
    case "GET /v1/health":
      return send(200, { ok: true });
    // A POST, because sending offers takes listings out of the market, and a cross-site GET needs no preflight.
    case "POST /v1/offers":
      return send(200, await market.postOffers(await readBody(request)));
    case "POST /v1/sales": {
      const { success } = await salesLimiter.limit({ key: address(request) });
      return success ? send(200, await market.postSales(await readBody(request), await sourceOf(request, secrets.IP_SALT))) : send(429, { error: "too many requests" });
    }
    default:
      return send(404, { error: "not found" });
  }
}

export async function handle(request: Request, market: Market, limiter: Limiter, salesLimiter: Limiter, secrets: Secrets = {}): Promise<Response> {
  try {
    const { success } = await limiter.limit({ key: address(request) });
    if (!success) {
      return send(429, { error: "too many requests" });
    }
    return await route(request, market, salesLimiter, secrets);
  } catch (error) {
    if (error instanceof BadRequest) {
      return send(400, { error: error.message });
    }
    console.error(`${request.method} ${new URL(request.url).pathname} failed:`, error);
    return send(500, { error: "server error" });
  }
}

function openMarket(env: Env): Market {
  return new Market(env.DB, { ...DEFAULT_CONFIG, allowSelf: env.ALLOW_SELF === "true", driftSecret: (env as Secrets).DRIFT_SALT });
}

export default {
  fetch(request, env) {
    return handle(request, openMarket(env), env.LIMITER, env.SALES_LIMITER, env as Secrets);
  },
  async scheduled(_controller, env) {
    await openMarket(env).prune();
  },
} satisfies ExportedHandler<Env>;

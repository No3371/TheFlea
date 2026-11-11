import assert from "node:assert/strict";
import { test } from "node:test";
import { categoryDrift } from "../src/drift.ts";

const HOUR = 3600;
const SECRET = "test-secret";
const AMPLITUDE = 0.22;

async function at(now, category = "FOOD", market = "0", secret = SECRET) {
  return (await categoryDrift(secret, AMPLITUDE, market, [category], now)).get(category);
}

test("there's no drift without a secret or an amplitude", async () => {
  assert.equal((await categoryDrift(undefined, AMPLITUDE, "0", ["FOOD"], 1000)).size, 0);
  assert.equal((await categoryDrift("", AMPLITUDE, "0", ["FOOD"], 1000)).size, 0);
  assert.equal((await categoryDrift(SECRET, 0, "0", ["FOOD"], 1000)).size, 0);
});

test("drift stays within the amplitude and uses most of it", async () => {
  const values = [];
  for (let hour = 0; hour < 200; hour++) {
    values.push(await at(hour * HOUR + 1234));
  }
  for (const value of values) {
    assert.ok(Math.abs(value) <= AMPLITUDE, `${value} is past the amplitude`);
  }
  assert.ok(Math.max(...values) > AMPLITUDE * 0.7, "never runs hot");
  assert.ok(Math.min(...values) < -AMPLITUDE * 0.7, "never runs cold");
});

test("the same moment gives the same drift", async () => {
  assert.equal(await at(123_456), await at(123_456));
});

test("markets, categories and secrets each drift on their own", async () => {
  const base = await at(123_456);
  assert.notEqual(await at(123_456, "TOOL"), base);
  assert.notEqual(await at(123_456, "FOOD", "friends"), base);
  assert.notEqual(await at(123_456, "FOOD", "0", "other-secret"), base);
});

test("drift glides through the hour without jumping on it", async () => {
  const hour = 500 * HOUR;
  const before = await at(hour - 1);
  const after = await at(hour + 1);
  assert.ok(Math.abs(after - before) < AMPLITUDE * 0.001, `jumped from ${before} to ${after}`);
  // Halfway between targets it sits halfway between them.
  const start = await at(hour);
  const end = await at(hour + HOUR);
  assert.ok(Math.abs((await at(hour + HOUR / 2)) - (start + end) / 2) < 1e-9);
});

test("each category is worked out once however often it's asked for", async () => {
  const drift = await categoryDrift(SECRET, AMPLITUDE, "0", ["FOOD", "FOOD", "TOOL"], 1000);
  assert.deepEqual([...drift.keys()].sort(), ["FOOD", "TOOL"]);
});

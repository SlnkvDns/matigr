import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { compose, eventually, session } from './transport-test-support.mjs';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const tournament = manifest.tournament.tournamentId;
const auth = await session(process.env.ADMISSION_TEAM ?? 'team-beta'); const foreign = await session('team-alpha'); const jury = await session('jury-a');
const base = 'http://127.0.0.1:5103/api/v1/appeals';
async function call(token, path = '', body, key = randomUUID()) {
  const r = await fetch(base + path, { method: body ? 'POST' : 'GET', headers: { Authorization: `Bearer ${token}`,
    ...(body ? { 'Content-Type': 'application/json', 'Idempotency-Key': key } : {}) }, ...(body ? { body: JSON.stringify(body) } : {}) });
  assert.equal(r.headers.get('Cache-Control'), 'private, no-store'); return r;
}
const own = await (await fetch(`http://127.0.0.1:5102/api/v1/results/me/tournaments/${tournament}/results`, { headers: { Authorization: `Bearer ${auth}` } })).json();
const candidates = own.results.filter(r => r.appealAvailabilityHint !== 'SeniorFinal');
const candidate = candidates.find(r => r.outcome === 'Incorrect') ?? candidates[0]; assert.ok(candidate);
const body = { resultId: candidate.resultId, expectedResultVersion: candidate.resultVersion };
assert.equal((await call(foreign, '', body)).status, 403);
assert.equal((await call(auth, '', { ...body, expectedResultVersion: String(BigInt(body.expectedResultVersion) + 1n) })).status, 409);
const keys = [randomUUID(), randomUUID()];
const race = await Promise.all(keys.map(key => call(auth, '', body, key)));
assert.deepEqual(race.map(r => r.status).sort(), [201, 409]);
const success = race.findIndex(r => r.status === 201); const first = await race[success].json(); const key = keys[success];
assert.equal(first.state, 'Registered'); assert.equal(first.version, '1'); assert.equal('recheckRequestId' in first, false);
assert.equal(race[success].headers.get('ETag'), '"1"'); assert.equal(race[success].headers.get('Location'), `/api/v1/appeals/${first.appealId}`);
assert.equal((await call(auth, '', { ...body, expectedResultVersion: '999' }, key)).status, 409);
assert.equal((await call(foreign, `/${first.appealId}`)).status, 404);
const queue = await (await call(jury, `/tournaments/${tournament}?state=Registered`)).json();
assert.ok(queue.items.some(a => a.appealId === first.appealId));
try {
  compose(['stop', 'results-api']);
  const replay = await call(auth, '', body, key); assert.equal(replay.status, 201); assert.deepEqual(await replay.json(), first);
  assert.equal((await call(auth, '', body)).status, 503, 'a new key cannot use cached Results basis');
} finally { compose(['start', 'results-api']); }
await eventually(async () => { try { return (await fetch('http://127.0.0.1:5102/health/ready')).status; } catch { return 0; } }, x => x === 200, 'Results restored');
const gammaAuth = await session('team-gamma');
const gamma = await (await fetch(`http://127.0.0.1:5102/api/v1/results/me/tournaments/${tournament}/results`, { headers: { Authorization: `Bearer ${gammaAuth}` } })).json();
const senior = gamma.results.find(r => r.appealAvailabilityHint === 'SeniorFinal');
assert.ok(senior, 'Run the V2 senior verdict scenario before this gate');
{
  const rejected = await call(gammaAuth, '', { resultId: senior.resultId, expectedResultVersion: senior.resultVersion });
  assert.equal(rejected.status, 409); assert.equal((await rejected.json()).code, 'BaseNotAppealable');
}
const document = JSON.parse(await readFile(new URL('../contracts/http/appeals/openapi.json', import.meta.url)));
function refs(value) {
  if (!value || typeof value !== 'object') return value;
  if (Array.isArray(value)) return value.map(refs);
  return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, k === '$ref' ? v.replace('#/components/schemas/', '#/$defs/') : refs(v)]));
}
const ajv = new Ajv2020({ strict: false }); addFormats(ajv);
assert.equal(ajv.validate({ ...refs(document.components.schemas.AppealView), $defs: refs(document.components.schemas) }, first), true, JSON.stringify(ajv.errors));
console.log('APL-IT-01: real user/service JWT + Results HTTP, foreign/stale rejection, concurrent Submit/permanent quota, Registered queue, lost reply before Results outage, no cached admission: OK');

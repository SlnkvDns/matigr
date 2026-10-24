import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { sql, quote as q, session } from './transport-test-support.mjs';
const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const tournament = manifest.tournament.tournamentId;
const tokens = [await session('jury-a'), await session('jury-b')];
const base = 'http://127.0.0.1:5103/api/v1/appeals';
async function get(path) { const r = await fetch(base + path, { headers: { Authorization: `Bearer ${tokens[0]}` } }); assert.equal(r.status, 200); return r.json(); }
const queue = await get(`/tournaments/${tournament}`); assert.ok(queue.items.length > 0, 'Submit a real Registered appeal first');
const a = queue.items[0]; const keys = [randomUUID(), randomUUID()];
async function claim(index, key, etag = '"1"', body = {}) {
  return fetch(`${base}/${a.appealId}/claim`, { method: 'POST', headers: { Authorization: `Bearer ${tokens[index]}`, 'Idempotency-Key': key,
    'Content-Type': 'application/json', ...(etag ? { 'If-Match': etag } : {}) }, body: JSON.stringify(body) });
}
assert.equal((await claim(0, randomUUID(), '')).status, 428);
assert.equal((await claim(0, randomUUID(), 'W/"1"')).status, 400);
assert.equal((await claim(0, randomUUID(), '"1"', { requestedReviewerId: manifest.subjects.find(s => s.testSubjectId === 'senior').subjectId })).status, 400);
const race = await Promise.all(keys.map((key, i) => claim(i, key)));
assert.deepEqual(race.map(r => r.status).sort(), [202, 412]); const winner = race.findIndex(r => r.status === 202);
const first = await race[winner].json(); assert.equal(first.state, 'AcceptedAwaitingRecheck'); assert.equal('checkId' in first, false);
const replay = await claim(winner, keys[winner]); assert.equal(replay.status, 202); assert.deepEqual(await replay.json(), first);
const current = await get(`/${a.appealId}`); assert.equal(current.state, 'AcceptedAwaitingRecheck'); assert.equal('checkId' in current, false);
assert.ok(!(await get(`/tournaments/${tournament}`)).items.some(x => x.appealId === a.appealId));
assert.equal(await sql('appeals', `SELECT count(*) FROM selection_attempts WHERE appeal_id=${q(a.appealId)}`), '1');
const selected = await sql('appeals', `SELECT reviewer_id FROM selection_attempts WHERE appeal_id=${q(a.appealId)}`);
assert.equal(selected, manifest.subjects.find(s => s.testSubjectId === (winner === 0 ? 'jury-a' : 'jury-b')).subjectId);
assert.equal(await sql('appeals', `SELECT count(*) FROM outbox WHERE envelope->'payload'->>'appealId'=${q(a.appealId)}`), '1');
console.log('APL-IT-02: real HTTP concurrent manual claim, 202 remains pending, exact subject selection, ETag/key replay, one durable attempt/outbox: OK');

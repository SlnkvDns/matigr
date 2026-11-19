import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { sql, quote as q, eventually, management, session } from './transport-test-support.mjs';
const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const tournament = manifest.tournament.tournamentId; const tokens = [await session('jury-a'), await session('jury-b')];
const base = 'http://127.0.0.1:5103/api/v1/appeals';
async function get(id) { const r = await fetch(`${base}/${id}`, { headers: { Authorization: `Bearer ${tokens[0]}` } }); assert.equal(r.status, 200); return r.json(); }
const first = await eventually(() => sql('appeals', "SELECT appeal_id FROM appeals WHERE state='RecheckPending' ORDER BY submitted_at LIMIT 1"), Boolean, 'Checking confirms saved pending attempt');
const detail = await get(first); assert.ok(detail.checkId);
const selected = await sql('appeals', `SELECT reviewer_id FROM selection_attempts WHERE request_id=${q(detail.recheckRequestId)}`);
const busyIndex = manifest.subjects.find(s => s.subjectId === selected).testSubjectId === 'jury-a' ? 0 : 1;
const queue = await get(`tournaments/${tournament}`); assert.ok(queue.items.length > 0);
const next = queue.items[0];
async function claim(index, version) {
  const r = await fetch(`${base}/${next.appealId}/claim`, { method: 'POST', headers: { Authorization: `Bearer ${tokens[index]}`, 'Idempotency-Key': randomUUID(), 'If-Match': `"${version}"` } });
  assert.equal(r.status, 202); return r.json();
}
await claim(busyIndex, next.version);
const busy = await eventually(() => get(next.appealId), x => x.state === 'Registered', 'confirmed ReviewerBusy returns same appeal');
assert.equal(await sql('checking', `SELECT count(*) FROM checks WHERE appeal_id=${q(next.appealId)}`), '0');
const oldRequest = busy.recheckRequestId;
await claim(1 - busyIndex, busy.version);
const assigned = await eventually(() => get(next.appealId), x => x.state === 'RecheckPending', 'new manual attempt uses free selected jury');
assert.notEqual(assigned.recheckRequestId, oldRequest); assert.ok(assigned.checkId);
const oldBusy = JSON.parse(await sql('checking', `SELECT response_envelope::text FROM recheck_request_receipts WHERE request_id=${q(oldRequest)}`));
oldBusy.messageId = randomUUID();
await management('exchanges/matigr/matigr.v1/publish', 'POST', { properties: { message_id: oldBusy.messageId, app_id: 'Checking', delivery_mode: 2, content_type: 'application/json' },
  routing_key: 'checking.recheck-request-rejected.v1', payload: JSON.stringify(oldBusy), payload_encoding: 'string' });
await eventually(() => sql('appeals', `SELECT count(*) FROM inbox WHERE message_id=${q(oldBusy.messageId)}`), x => x === '1', 'old busy recorded without regression');
assert.deepEqual(await get(next.appealId), assigned);
assert.equal(await sql('checking', `SELECT count(*) FROM checks WHERE appeal_id=${q(next.appealId)}`), '1');
assert.equal(await sql('appeals', `SELECT count(*) FROM selection_attempts WHERE appeal_id=${q(next.appealId)}`), '2');
console.log('APL-INT-03: real outbox/Checking/inbox assignment, busy→same Registered→new manual request, stale busy does not cancel current Check: OK');

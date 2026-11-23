import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { eventually, sql } from './transport-test-support.mjs';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

assert.ok(process.env.COMPOSE_PROJECT_NAME, 'Select a fresh isolated Compose project explicitly');
const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const tournamentId = manifest.tournament.tournamentId;
const base = 'http://127.0.0.1:5101/api/v1/checking';
async function token(testSubjectId) {
  const r = await fetch('http://127.0.0.1:5101/dev/v1/session/select', { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ testSubjectId }) });
  assert.equal(r.status, 200); return (await r.json()).accessToken;
}
async function request(auth, path, body, etag, key = randomUUID()) {
  return fetch(base + path, { method: body ? 'POST' : 'GET', headers: {
    Authorization: `Bearer ${auth}`, ...(body ? { 'Content-Type': 'application/json', 'Idempotency-Key': key } : {}),
    ...(etag ? { 'If-Match': etag } : {}) }, ...(body ? { body: JSON.stringify(body) } : {}) });
}
async function current(auth) { const r = await request(auth, '/me/assignment'); assert.equal(r.status, 200); return { body: await r.json(), etag: r.headers.get('ETag') }; }
const ordinary = await token('jury-a'), senior = await token('senior');
const first = await current(ordinary), seniorOrdinary = await current(senior);
const path = `/checks/${first.body.check.checkId}/refuse`;
const body = { assignmentId: first.body.assignmentId, capability: first.body.capability };
const key = randomUUID();
const refusal = await request(ordinary, path, body, first.etag, key); assert.equal(refusal.status, 200);
const queued = await refusal.json(); assert.equal(queued.state, 'SeniorQueued');
assert.equal((await request(ordinary, '/me/assignment')).status, 204);
const replay = await request(ordinary, path, body, first.etag, key); assert.equal(replay.status, 200);
assert.deepEqual(await replay.json(), queued);
assert.equal((await request(ordinary, path, body, first.etag)).status, 412);
assert.equal((await request(ordinary, `/checks/${queued.checkId}/material`)).status, 403);
const next = await request(ordinary, '/me/assignments/next', { tournamentId, queue: 'general' }); assert.equal(next.status, 200);
const completedOrdinary = await request(senior, `/checks/${seniorOrdinary.body.check.checkId}/complete`,
  { assignmentId: seniorOrdinary.body.assignmentId, capability: seniorOrdinary.body.capability, outcome: 'Correct' }, seniorOrdinary.etag);
assert.equal(completedOrdinary.status, 200); assert.equal((await completedOrdinary.json()).decisionOrigin, 'OrdinaryReview');
const seniorClaim = await request(senior, '/me/assignments/next', { tournamentId, queue: 'senior' }); assert.equal(seniorClaim.status, 200);
const assignment = await seniorClaim.json(); assert.equal(assignment.check.checkId, queued.checkId);
const final = await request(senior, `/checks/${queued.checkId}/complete`,
  { assignmentId: assignment.assignmentId, capability: assignment.capability, outcome: 'Incorrect' }, seniorClaim.headers.get('ETag'));
assert.equal(final.status, 200); const verdict = await final.json();
assert.equal(verdict.state, 'Completed'); assert.equal(verdict.decisionOrigin, 'SeniorEscalation');
assert.equal((await request(senior, '/me/assignment')).status, 204);

// Transport is enabled in Compose now. Confirmed delivery is expected, not permanent Pending.
const facts = await eventually(async () => JSON.parse(await sql('checking', "SELECT COALESCE(json_agg(json_build_object('envelope', envelope, 'status', status)), '[]'::json) FROM outbox;")),
  rows => rows.length === 2 && rows.every(x => x.status === 'Sent'), 'confirmed runtime CheckCompleted');
const ajv = new Ajv2020({ strict: false, allErrors: true }); addFormats(ajv);
const validate = ajv.compile(JSON.parse(await readFile(new URL('../contracts/events/checking/check-completed.v1.schema.json', import.meta.url))));
for (const fact of facts) { assert.equal(fact.status, 'Sent'); assert.ok(validate(fact.envelope), 'Invalid runtime CheckCompleted'); }
console.log('Checking transitions: real refuse/replay/stale ETag, immediate release, same Check senior final and durable schema-valid outbox: OK');

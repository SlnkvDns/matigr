import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { compose, sql, quote, eventually, management, session } from './transport-test-support.mjs';

const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const tournamentId = manifest.tournament.tournamentId;
const auth = await session('jury-a');
const base = 'http://127.0.0.1:5101/api/v1/checking';
async function command(path, body, etag, key = randomUUID()) {
  return fetch(base + path, { method: 'POST', headers: { Authorization: `Bearer ${auth}`, 'Content-Type': 'application/json',
    'Idempotency-Key': key, ...(etag ? { 'If-Match': etag } : {}) }, body: JSON.stringify(body) });
}
let claim = await fetch(base + '/me/assignment', { headers: { Authorization: `Bearer ${auth}` } });
if (claim.status === 204) claim = await command('/me/assignments/next', { tournamentId, queue: 'general' });
assert.equal(claim.status, 200);
const assignment = await claim.json(); const checkId = assignment.check.checkId;
const verdict = { assignmentId: assignment.assignmentId, capability: assignment.capability, outcome: 'Correct' };
const key = randomUUID();
try {
  compose(['stop', 'rabbitmq']);
  const completed = await command(`/checks/${checkId}/complete`, verdict, claim.headers.get('ETag'), key);
  assert.equal(completed.status, 200);
  assert.equal(await sql('checking', `SELECT status FROM outbox WHERE envelope->'payload'->>'checkId' = ${quote(checkId)}`), 'Pending');
  assert.equal(await sql('results', `SELECT count(*) FROM evaluation_receipts WHERE evaluation_id = ${quote(checkId)}`), '0');
} finally { compose(['start', 'rabbitmq']); }
await eventually(() => sql('results', `SELECT status FROM evaluation_receipts WHERE evaluation_id = ${quote(checkId)}`), x => x === 'Applied', 'published receipt after broker restart');
await eventually(() => sql('checking', `SELECT status FROM outbox WHERE envelope->'payload'->>'checkId' = ${quote(checkId)}`), x => x === 'Sent', 'confirmed outbox');
const replay = await command(`/checks/${checkId}/complete`, verdict, claim.headers.get('ETag'), key); assert.equal(replay.status, 200);
assert.equal(await sql('results', `SELECT count(*) FROM result_publications WHERE result_id IN (SELECT result_id FROM evaluation_receipts WHERE evaluation_id = ${quote(checkId)})`), '1');

const route = 'checking.recheck-assigned.v1';
const bindings = await management('bindings/matigr/e/matigr.v1/q/appeals.checking-facts.v1');
const binding = bindings.find(x => x.routing_key === route); assert.ok(binding);
const event = JSON.parse(await readFile(new URL('../tests/contracts/fixtures/checking/recheck-assigned.selected.json', import.meta.url)));
event.messageId = randomUUID(); const message = event.messageId;
try {
  await management(`bindings/matigr/e/matigr.v1/q/appeals.checking-facts.v1/${encodeURIComponent(binding.properties_key)}`, 'DELETE');
  await sql('checking', `INSERT INTO outbox (message_id,envelope,routing_key,correlation_id,status,attempts,created_at,next_attempt_at) VALUES (${quote(message)},${quote(JSON.stringify(event))}::jsonb,${quote(route)},${quote(randomUUID())},'Pending',0,now(),now());`);
  await eventually(() => sql('checking', `SELECT CASE WHEN attempts > 0 AND locked_until IS NULL THEN status ELSE '' END FROM outbox WHERE message_id=${quote(message)}`), x => x === 'Pending', 'mandatory return attempted');
  assert.equal(await sql('checking', `SELECT status FROM outbox WHERE message_id=${quote(message)}`), 'Pending');
} finally { await management('bindings/matigr/e/matigr.v1/q/appeals.checking-facts.v1', 'POST', { routing_key: route, arguments: {} }); }
await eventually(() => sql('checking', `SELECT status FROM outbox WHERE message_id=${quote(message)}`), x => x === 'Sent', 'restored route retried');

const poison = randomUUID(); const raw = JSON.stringify({ messageId: poison, schemaVersion: 99 });
const published = await management('exchanges/matigr/matigr.v1/publish', 'POST', { properties: { message_id: poison, app_id: 'Appeals', delivery_mode: 2, content_type: 'application/json' }, routing_key: 'appeals.recheck-requested.v1', payload: raw, payload_encoding: 'string' });
assert.equal(published.routed, true);
const failure = await eventually(() => sql('checking', `SELECT failure_id FROM processing_failures WHERE message_id=${quote(poison)} AND state='Quarantined'`), Boolean, 'poison quarantine');
compose(['exec', '-T', 'checking-api', 'sh', '/app/entrypoint.sh', `--Transport:Redrive=${failure}`]);
await eventually(() => sql('checking', `SELECT state FROM processing_failures WHERE failure_id=${quote(failure)}`), x => x === 'Quarantined', 'invalid payload re-quarantined');
assert.equal(await sql('checking', `SELECT count(*) FROM transport_redrives WHERE failure_id=${quote(failure)} AND message_id=${quote(poison)}`), '1');
const appeal = JSON.parse(await readFile(new URL('../tests/contracts/fixtures/checking/check-completed.appeal-senior.json', import.meta.url)));
const identities = new Map();
function canonical(value) {
  for (const [name, field] of Object.entries(value)) {
    if (field && typeof field === 'object') canonical(field);
    else if (name.endsWith('Id')) { if (!identities.has(field)) identities.set(field, randomUUID()); value[name] = identities.get(field); }
  }
}
canonical(appeal);
await management('exchanges/matigr/matigr.v1/publish', 'POST', { properties: { message_id: appeal.messageId, app_id: 'Checking', delivery_mode: 2, content_type: 'application/json' },
  routing_key: 'checking.check-completed.appeal.v1', payload: JSON.stringify(appeal), payload_encoding: 'string' });
await eventually(() => sql('results', `SELECT reason_code FROM processing_failures WHERE message_id=${quote(appeal.messageId)} AND state='Deferred'`), x => x === 'AppealHandlerNotImplemented', 'Results owns its independent Appeal copy');
const copies = await management('queues/matigr/appeals.checking-facts.v1/get', 'POST', { count: 100, ackmode: 'ack_requeue_true', encoding: 'auto' });
assert.ok(copies.some(copy => JSON.parse(copy.payload).messageId === appeal.messageId), 'Appeals did not receive its separate copy');
console.log('Transport: broker restart/confirm, mandatory return, poison/audited redrive, separate CheckCompleted copies for Results and Appeals: OK');

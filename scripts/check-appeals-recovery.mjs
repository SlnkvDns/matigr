import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { compose, sql, quote as q, eventually, management } from './transport-test-support.mjs';
const before = await sql('appeals', 'SELECT count(*) FROM appeals');
const assignments = await sql('checking', "SELECT string_agg(assignment_id::text,',' ORDER BY assignment_id) FROM assignments WHERE state='Active'");
async function ready() { try { const r = await fetch('http://127.0.0.1:5103/health/ready'); return { status: r.status, body: await r.json() }; } catch { return {}; } }
try {
  compose(['stop', 'rabbitmq']);
  const state = await eventually(ready, r => r.status === 503 && r.body?.broker === false, 'Appeals reports broker outage');
  assert.equal(state.body.database, true);
} finally { compose(['start', 'rabbitmq']); }
await eventually(ready, r => r.status === 200 && r.body?.broker === true, 'both Appeals consumers recovered');
assert.equal(await sql('checking', "SELECT string_agg(assignment_id::text,',' ORDER BY assignment_id) FROM assignments WHERE state='Active'"), assignments);
const message = randomUUID(); const raw = JSON.stringify({ messageId: message, schemaVersion: 99 });
assert.equal((await management('exchanges/matigr/matigr.v1/publish', 'POST', { properties: { message_id: message, app_id: 'Checking', delivery_mode: 2, content_type: 'application/json' },
  routing_key: 'checking.recheck-assigned.v1', payload: raw, payload_encoding: 'string' })).routed, true);
const failure = await eventually(() => sql('appeals', `SELECT failure_id FROM processing_failures WHERE message_id=${q(message)} AND state='Quarantined'`), Boolean, 'Appeals poison quarantined before ack');
assert.equal(await sql('appeals', `SELECT envelope FROM processing_failures WHERE failure_id=${q(failure)}`), raw);
compose(['exec', '-T', 'appeals-api', 'sh', '/app/entrypoint.sh', `--Transport:Redrive=${failure}`]);
await eventually(() => sql('appeals', `SELECT state FROM processing_failures WHERE failure_id=${q(failure)}`), x => x === 'Quarantined', 'invalid redrive remains observable');
assert.equal(await sql('appeals', `SELECT count(*) FROM transport_redrives WHERE failure_id=${q(failure)} AND message_id=${q(message)}`), '1');
assert.equal(await sql('appeals', `SELECT count(*) FROM inbox WHERE message_id=${q(message)}`), '0');
assert.equal(await sql('appeals', 'SELECT count(*) FROM appeals'), before);
console.log('APL-INT-07: real broker outage/readiness/recovery, unchanged assignments, poison/raw quarantine and audited identity-preserving redrive without new quota: OK');

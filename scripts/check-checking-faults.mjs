import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { compose, sql, quote, eventually, session, databaseBarrier } from './transport-test-support.mjs';
import { waitForHttpReady } from './http-test-support.mjs';

const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const auth = await session('jury-a');
const base = 'http://127.0.0.1:5101/api/v1/checking';
async function post(path, body, key, etag) {
  return fetch(base + path, { method: 'POST', headers: { Authorization: `Bearer ${auth}`, 'Content-Type': 'application/json',
    'Idempotency-Key': key, ...(etag ? { 'If-Match': etag } : {}) }, body: JSON.stringify(body), signal: AbortSignal.timeout(15000) });
}
const claim = await post('/me/assignments/next', { tournamentId: manifest.tournament.tournamentId, queue: 'general' }, randomUUID());
assert.equal(claim.status, 200);
const assignment = await claim.json(); const check = assignment.check.checkId;
const verdict = { assignmentId: assignment.assignmentId, capability: assignment.capability, outcome: 'Correct' };
const key = randomUUID();
const release = await databaseBarrier('checking', 730104);
try {
  // The worker has received broker confirm, but its Sent transaction cannot commit yet.
  await sql('checking', `CREATE FUNCTION matigr_fault_sent() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
    IF NEW.status='Sent' AND NEW.envelope->'payload'->>'checkId'=${quote(check)} THEN PERFORM pg_advisory_xact_lock(730104); END IF; RETURN NEW; END $$;
    CREATE TRIGGER matigr_fault_sent BEFORE UPDATE ON outbox FOR EACH ROW EXECUTE FUNCTION matigr_fault_sent();`);
  assert.equal((await post(`/checks/${check}/complete`, verdict, key, claim.headers.get('ETag'))).status, 200);
  await eventually(() => sql('checking', "SELECT count(*) FROM pg_stat_activity WHERE wait_event='advisory' AND query LIKE 'UPDATE outbox SET status%'") , x => Number(x) > 0, 'publisher blocked after confirm');
  await eventually(() => sql('results', `SELECT status FROM evaluation_receipts WHERE evaluation_id=${quote(check)}`), x => x === 'Applied', 'broker delivery committed before publisher crash');
  const message = await sql('checking', `SELECT message_id FROM outbox WHERE envelope->'payload'->>'checkId'=${quote(check)}`);
  assert.equal(await sql('checking', `SELECT status FROM outbox WHERE message_id=${quote(message)}`), 'Pending');
  compose(['kill', '-s', 'SIGKILL', 'checking-api']);
  await release();
  await sql('checking', 'DROP TRIGGER matigr_fault_sent ON outbox; DROP FUNCTION matigr_fault_sent();');
  compose(['start', 'checking-api']);
  await eventually(() => sql('checking', `SELECT status FROM outbox WHERE message_id=${quote(message)}`), x => x === 'Sent', 'same message recovered after expired publisher lease');
  assert.equal(await sql('checking', `SELECT count(*) FROM outbox WHERE envelope->'payload'->>'checkId'=${quote(check)}`), '1');
  assert.equal(await sql('results', `SELECT count(*) FROM result_publications WHERE result_id IN (SELECT result_id FROM evaluation_receipts WHERE evaluation_id=${quote(check)})`), '1');
  await waitForHttpReady('http://127.0.0.1:5101/health/ready', 'Checking recovered');
  const replay = await post(`/checks/${check}/complete`, verdict, key, claim.headers.get('ETag'));
  assert.equal(replay.status, 200, 'lost HTTP reply replays after crash with original ETag');
} finally {
  await release().catch(() => {});
  await sql('checking', 'DROP TRIGGER IF EXISTS matigr_fault_sent ON outbox; DROP FUNCTION IF EXISTS matigr_fault_sent();');
  compose(['start', 'checking-api']);
}
console.log('CHK-IT-04: real confirm→Sent crash, lease recovery, stable message identity, duplicate delivery and HTTP replay: OK');

import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { compose, sql, quote as q, eventually, management, session, databaseBarrier } from './transport-test-support.mjs';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const available = await sql('checking', "SELECT solution_id FROM checks c WHERE kind='Initial' AND state='Completed' AND origin='OrdinaryReview' AND NOT EXISTS (SELECT 1 FROM checks a WHERE a.solution_id=c.solution_id AND a.kind='Appeal') ORDER BY solution_id LIMIT 1");
const solution = manifest.solutions.find(s => s.solutionId === available); assert.ok(solution, 'Need a completed ordinary fixture without a prior Appeal Check');
const original = JSON.parse(await sql('checking', `SELECT row_to_json(c)::text FROM checks c WHERE solution_id=${q(solution.solutionId)} AND kind='Initial' AND state='Completed'`));
assert.equal(original.origin, 'OrdinaryReview', 'Run after primary verdict scenarios');
const basis = JSON.parse(await sql('results', `SELECT row_to_json(r)::text FROM team_task_results r WHERE solution_id=${q(solution.solutionId)}`));
const event = JSON.parse(await readFile(new URL('../tests/contracts/fixtures/appeals/recheck-requested.json', import.meta.url)));
event.messageId = randomUUID(); event.aggregateId = randomUUID(); event.correlationId = event.aggregateId;
Object.assign(event.payload, { ...Object.fromEntries(['solutionId', 'materialVersionId', 'tournamentId', 'teamId', 'taskId'].map(k => [k, solution[k]])),
  appealId: event.aggregateId, recheckRequestId: randomUUID(), resultId: basis.result_id, expectedResultVersion: String(basis.result_version),
  publicationId: basis.publication_id, baseCheckId: original.check_id, requestedReviewerId: original.current_assignment_id ?
    await sql('checking', `SELECT reviewer_id FROM assignments WHERE assignment_id=${q(original.current_assignment_id)}`) : '',
  baseOutcome: original.outcome, submittedAt: new Date().toISOString(), acceptedAt: new Date().toISOString() });
const p = event.payload;
async function publish(value) {
  assert.equal((await management('exchanges/matigr/matigr.v1/publish', 'POST', { properties: { message_id: value.messageId, app_id: 'Appeals', delivery_mode: 2, content_type: 'application/json' },
    routing_key: 'appeals.recheck-requested.v1', payload: JSON.stringify(value), payload_encoding: 'string' })).routed, true);
}
const release = await databaseBarrier('checking', 730103);
let check;
try {
  await sql('checking', `CREATE FUNCTION matigr_recheck_sent() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
    IF NEW.status='Sent' AND NEW.envelope->'payload'->>'appealId'=${q(p.appealId)} THEN PERFORM pg_advisory_xact_lock(730103); END IF; RETURN NEW; END $$;
    CREATE TRIGGER matigr_recheck_sent BEFORE UPDATE ON outbox FOR EACH ROW EXECUTE FUNCTION matigr_recheck_sent();`);
  await publish(event);
  check = await eventually(() => sql('checking', `SELECT check_id FROM checks WHERE appeal_id=${q(p.appealId)}`), Boolean, 'personal Appeal Check created');
  await eventually(() => sql('checking', "SELECT count(*) FROM pg_stat_activity WHERE wait_event='advisory' AND query LIKE 'UPDATE outbox SET status%'"), x => Number(x) > 0, 'Assigned confirmed before publisher crash');
  compose(['kill', '-s', 'SIGKILL', 'checking-api']); await release();
  await sql('checking', 'DROP TRIGGER matigr_recheck_sent ON outbox; DROP FUNCTION matigr_recheck_sent();');
  compose(['start', 'checking-api']);
  await eventually(() => sql('checking', `SELECT status FROM outbox WHERE correlation_id=${q(p.appealId)}`), x => x === 'Sent', 'same Assigned message recovered');
} finally {
  await release(); await sql('checking', 'DROP TRIGGER IF EXISTS matigr_recheck_sent ON outbox; DROP FUNCTION IF EXISTS matigr_recheck_sent();');
  compose(['start', 'checking-api']);
}
await publish(event); const duplicate = structuredClone(event); duplicate.messageId = randomUUID(); await publish(duplicate);
await eventually(() => sql('checking', `SELECT count(*) FROM inbox WHERE message_id=${q(duplicate.messageId)}`), x => x === '1', 'request duplicate durable');
assert.equal(await sql('checking', `SELECT count(*) FROM checks WHERE appeal_id=${q(p.appealId)}`), '1');
const profile = manifest.subjects.find(s => s.subjectId === p.requestedReviewerId); assert.ok(profile);
const auth = await session(profile.testSubjectId);
const seniorAuth = await session('senior');
const base = 'http://127.0.0.1:5101/api/v1/checking';
async function current(token) {
  const r = await fetch(base + '/me/assignment', { headers: { Authorization: `Bearer ${token}` } }); assert.equal(r.status, 200);
  return { body: await r.json(), etag: r.headers.get('ETag') };
}
async function command(token, path, body, etag) {
  return fetch(base + path, { method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID(),
    ...(etag ? { 'If-Match': etag } : {}) }, body: JSON.stringify(body) });
}
const assigned = await current(auth); assert.equal(assigned.body.check.checkId, check); assert.equal(assigned.body.reviewerId, p.requestedReviewerId);
assert.equal((await command(auth, `/checks/${check}/refuse`, { assignmentId: assigned.body.assignmentId, capability: assigned.body.capability }, assigned.etag)).status, 200);
assert.equal((await fetch(base + '/me/assignment', { headers: { Authorization: `Bearer ${auth}` } })).status, 204);
const senior = await command(seniorAuth, '/me/assignments/next', { tournamentId: p.tournamentId, queue: 'senior' }); assert.equal(senior.status, 200);
const final = await senior.json(); assert.equal(final.check.checkId, check); assert.notEqual(final.assignmentId, assigned.body.assignmentId);
assert.equal((await fetch(`${base}/checks/${check}/material`, { headers: { Authorization: `Bearer ${auth}` } })).status, 403);
const outcome = p.baseOutcome === 'Correct' ? 'Incorrect' : 'Correct';
assert.equal((await command(seniorAuth, `/checks/${check}/complete`, { assignmentId: final.assignmentId, capability: final.capability, outcome }, senior.headers.get('ETag'))).status, 200);
const raw = await eventually(() => sql('results', `SELECT response::text FROM evaluation_receipts WHERE evaluation_id=${q(check)}`), Boolean, 'Results senior receipt');
const receipt = JSON.parse(raw); const ajv = new Ajv2020({ strict: false }); addFormats(ajv);
const schema = JSON.parse(await readFile(new URL('../contracts/events/results/result-evaluation-applied.v1.schema.json', import.meta.url)));
assert.equal(ajv.validate(schema, receipt), true, JSON.stringify(ajv.errors));
assert.equal(receipt.payload.changed, true); assert.equal(receipt.payload.evaluationDecisionOrigin, 'SeniorEscalation');
assert.equal(receipt.payload.selectedReviewerId, p.requestedReviewerId); assert.equal(receipt.payload.reviewerId, final.reviewerId);
assert.equal(receipt.payload.basePublicationId, p.publicationId); assert.notEqual(receipt.payload.publicationId, p.publicationId);
assert.equal(await sql('checking', `SELECT count(*) FROM checks WHERE appeal_id=${q(p.appealId)}`), '1');
assert.equal(await sql('checking', `SELECT count(*) FROM assignments WHERE check_id=${q(check)}`), '2');
await publish(event);
assert.equal(await sql('checking', `SELECT state FROM checks WHERE check_id=${q(check)}`), 'Completed');
console.log('Recheck runtime: broker request/replay→original selected reviewer→refusal→same Check/senior→Results Published receipt/lineage: OK');

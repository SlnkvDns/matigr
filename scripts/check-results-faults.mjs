import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { compose, sql, quote, eventually, management } from './transport-test-support.mjs';

const event = JSON.parse(await readFile(new URL('../tests/contracts/fixtures/checking/check-completed.initial.json', import.meta.url)));
const p = event.payload;
for (const key of Object.keys(p).filter(x => x.endsWith('Id'))) p[key] = randomUUID();
event.messageId = randomUUID(); event.aggregateId = p.checkId;
const t = p.tournamentId; const q = quote;
await sql('results', `INSERT INTO tournament_snapshots (tournament_id,version,updated_at,manifest_hash,fixture_version) VALUES (${q(t)},0,now(),'fault-test',1);
  INSERT INTO tournament_teams (tournament_id,team_id,name) VALUES (${q(t)},${q(p.teamId)},'Fault test');
  INSERT INTO tournament_tasks (tournament_id,task_id,name,"order") VALUES (${q(t)},${q(p.taskId)},'Prepared task',1);`);
async function publish(value) {
  assert.equal((await management('exchanges/matigr/matigr.v1/publish', 'POST', { properties: { message_id: value.messageId, app_id: 'Checking', delivery_mode: 2, content_type: 'application/json' },
    routing_key: 'checking.check-completed.initial.v1', payload: JSON.stringify(value), payload_encoding: 'string' })).routed, true);
}
await publish(event);
const failure = await eventually(() => sql('results', `SELECT failure_id FROM processing_failures WHERE message_id=${q(event.messageId)} AND state='Deferred'`), Boolean, 'missing rule durably deferred');
assert.equal(await sql('results', `SELECT count(*) FROM evaluation_receipts WHERE evaluation_id=${q(p.checkId)}`), '0');
const absent = await (await fetch(`http://127.0.0.1:5102/api/v1/results/tournaments/${t}/standings`)).json();
assert.equal(absent.teams[0].cells[0].status, 'Pending');
assert.equal('score' in absent.teams[0].cells[0], false);
// Exercise the configured retry boundary without waiting through each backoff interval.
await sql('results', `UPDATE processing_failures SET attempts=7,next_attempt_at=now() WHERE failure_id=${q(failure)}`);
await eventually(() => sql('results', `SELECT state FROM processing_failures WHERE failure_id=${q(failure)}`), x => x === 'Quarantined', 'bounded retry quarantine');
compose(['restart', 'results-api']);
await sql('results', `INSERT INTO scoring_rules (tournament_id,task_id,rule_version,points,manifest_hash) VALUES (${q(t)},${q(p.taskId)},1,10,'fault-test')`);
compose(['exec', '-T', 'results-api', 'sh', '/app/entrypoint.sh', `--Transport:Redrive=${failure}`]);
await eventually(() => sql('results', `SELECT status FROM evaluation_receipts WHERE evaluation_id=${q(p.checkId)}`), x => x === 'Applied', 'redrive after rule recovery');
assert.equal(await sql('results', `SELECT count(*) FROM transport_redrives WHERE failure_id=${q(failure)} AND message_id=${q(event.messageId)}`), '1');
const before = await sql('results', `SELECT response::text FROM evaluation_receipts WHERE evaluation_id=${q(p.checkId)}`);
await publish(event);
const duplicate = structuredClone(event); duplicate.messageId = randomUUID(); await publish(duplicate);
await eventually(() => sql('results', `SELECT count(*) FROM inbox WHERE message_id=${q(duplicate.messageId)}`), x => x === '1', 'new message same evaluation replay');
assert.equal(await sql('results', `SELECT response::text FROM evaluation_receipts WHERE evaluation_id=${q(p.checkId)}`), before);
assert.equal(await sql('results', `SELECT count(*) FROM result_publications WHERE result_id IN (SELECT result_id FROM evaluation_receipts WHERE evaluation_id=${q(p.checkId)})`), '1');
assert.equal(await sql('results', `SELECT version FROM tournament_snapshots WHERE tournament_id=${q(t)}`), '1');
const poison = structuredClone(event); poison.messageId = randomUUID(); poison.schemaVersion = 99; await publish(poison);
await eventually(() => sql('results', `SELECT state FROM processing_failures WHERE message_id=${q(poison.messageId)}`), x => x === 'Quarantined', 'unknown schema quarantined');
assert.equal(await sql('results', `SELECT count(*) FROM inbox WHERE message_id=${q(poison.messageId)}`), '0');
const final = await (await fetch(`http://127.0.0.1:5102/api/v1/results/tournaments/${t}/standings`)).json();
assert.equal(final.snapshotVersion, '1'); assert.deepEqual(final.teams[0].cells[0], { status: 'Published', score: '10' });
console.log('RES-IT-02: missing scoring rule→durable defer→quarantine→restart→audited redrive, stable receipt, duplicate message/evaluation and poison schema: OK');

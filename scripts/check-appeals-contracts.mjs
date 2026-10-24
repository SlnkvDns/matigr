import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { sql } from './transport-test-support.mjs';
async function validator(path) { const ajv = new Ajv2020({ strict: false }); addFormats(ajv); return ajv.compile(JSON.parse(await readFile(new URL('../contracts/events/' + path, import.meta.url)))); }
let total = 0; let senior = 0;
for (const [service, producer, consumer, names] of [
  ['appeals','appeals','checking',['recheck-requested']],
  ['checking','checking','appeals',['recheck-assigned','recheck-request-rejected','recheck-escalated','check-completed']],
  ['results','results','appeals',['result-evaluation-applied','result-evaluation-rejected']],
]) {
  const rows = JSON.parse(await sql(service, "SELECT COALESCE(json_agg(envelope),'[]'::json)::text FROM outbox"));
  for (const name of names) {
    const matching = rows.filter(x => x.messageType.endsWith('.'+name) && (name !== 'check-completed' || x.payload.kind === 'Appeal'));
    if (name === 'recheck-requested' || name === 'recheck-assigned' || name === 'result-evaluation-applied') assert.ok(matching.length, 'Missing live '+name);
    for (const e of matching) {
      const file = `${name}.v1.schema.json`; const p = await validator(`${producer}/${file}`), c = await validator(`${consumer}/consumers/${file}`);
      assert.equal(p(e), true, JSON.stringify(p.errors)); assert.equal(c(e), true, JSON.stringify(c.errors));
      const unknown = structuredClone(e); unknown.schemaVersion = 99; assert.equal(c(unknown), false);
      const leaked = structuredClone(e); leaked.payload.capability = 'forbidden'; assert.equal(c(leaked), false);
      if (e.payload.assignmentKind === 'SeniorEscalation' || e.payload.evaluationDecisionOrigin === 'SeniorEscalation' || e.payload.decisionOrigin === 'SeniorEscalation') {
        ++senior; assert.ok(e.payload.escalationId); assert.ok(e.payload.selectedReviewerId);
        assert.notEqual(e.payload.reviewerId,e.payload.selectedReviewerId);
        const missing = structuredClone(e); delete missing.payload.escalationId; assert.equal(c(missing),false);
      }
      ++total;
    }
  }
}
if (process.argv.includes('--senior')) assert.ok(senior >= 3, 'Need actual senior Assigned/Completed/Applied');
console.log(`Appeals runtime producer/independent consumer gate: ${total} actual durable envelopes, ${senior} senior facts; unknown version/private field/missing escalation rejected: OK`);

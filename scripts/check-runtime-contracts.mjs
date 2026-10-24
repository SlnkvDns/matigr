import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { sql } from './transport-test-support.mjs';

async function validate(path, value) {
  const schema = JSON.parse(await readFile(new URL('../' + path, import.meta.url)));
  // Independent producer and consumer copies may deliberately have matching schema IDs.
  const checker = new Ajv2020({ strict: false }); addFormats(checker);
  assert.equal(checker.validate(schema, value), true, `${path}: ${JSON.stringify(checker.errors)}`);
}
const receipts = JSON.parse(await sql('results', "SELECT COALESCE(json_agg(response),'[]'::json)::text FROM evaluation_receipts WHERE response->'payload'->>'kind'='Appeal'"));
assert.ok(receipts.length > 0, 'Run after the real recheck broker scenario');
for (const receipt of receipts) {
  const applied = receipt.messageType === 'matigr.results.result-evaluation-applied';
  const name = `result-evaluation-${applied ? 'applied' : 'rejected'}.v1.schema.json`;
  await validate(`contracts/events/results/${name}`, receipt);
  await validate(`contracts/events/appeals/consumers/${name}`, receipt);
  assert.equal(receipt.aggregateId, receipt.payload.evaluationId);
  if (applied) {
    assert.equal(receipt.payload.publicationState, 'Published');
    if (!receipt.payload.changed) assert.equal(receipt.payload.publicationId, receipt.payload.basePublicationId);
  }
  const invalid = structuredClone(receipt); invalid.schemaVersion = 99;
  await assert.rejects(() => validate(`contracts/events/appeals/consumers/${name}`, invalid));
}
const completed = JSON.parse(await sql('checking', "SELECT COALESCE(json_agg(envelope),'[]'::json)::text FROM outbox WHERE routing_key='checking.check-completed.appeal.v1'"));
assert.ok(completed.length > 0);
for (const envelope of completed) {
  await validate('contracts/events/results/consumers/check-completed.v1.schema.json', envelope);
  await validate('contracts/events/appeals/consumers/check-completed.v1.schema.json', envelope);
}
console.log(`Runtime contract gate: ${receipts.length} durable Results receipts + ${completed.length} CheckCompleted validated independently by producer/consumers: OK`);

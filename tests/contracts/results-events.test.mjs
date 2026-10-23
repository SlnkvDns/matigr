import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkingCatalog, resultsCatalog, schemas, json, validateMessage } from './support.mjs';

const cases = [
  ['ResultEvaluationApplied', 'results/result-evaluation-applied.no-op.json'],
  ['ResultEvaluationApplied', 'results/result-evaluation-applied.senior.json'],
  ['ResultEvaluationRejected', 'results/result-evaluation-rejected.conflict.json'],
];
const fixtures = await Promise.all(cases.map(async ([name, file]) => [name, file, await json(`tests/contracts/fixtures/${file}`)]));

for (const [name, file, message] of fixtures) {
  test(`Results positive wire roundtrip: ${file}`, () => {
    assert.deepEqual(validateMessage(name, JSON.parse(JSON.stringify(message)), { producer: true }), message);
  });
  test(`Results required envelope and payload fields: ${file}`, () => {
    for (const key of schemas.get(name).required) {
      const changed = structuredClone(message); delete changed[key];
      assert.throws(() => validateMessage(name, changed), `${file}: missing envelope ${key}`);
    }
    for (const key of schemas.get(name).properties.payload.required) {
      const changed = structuredClone(message); delete changed.payload[key];
      assert.throws(() => validateMessage(name, changed), `${file}: missing payload ${key}`);
    }
  });
  test(`Results version, UTC and private-field boundaries: ${file}`, () => {
    for (const [field, bad] of [['schemaVersion', 2], ['producer', 'Checking'], ['aggregateType', 'Result'],
      ['aggregateVersion', '2'], ['occurredAt', '2026-10-23T04:00:00+06:00']])
      assert.throws(() => validateMessage(name, { ...message, [field]: bad }), `${file}: ${field}`);
    for (const field of ['capability', 'assignmentToken', 'token', 'accessToken', 'comment', 'commentary', 'material', 'materialUrl', 'requestId']) {
      const changed = structuredClone(message); changed.payload[field] = 'must-not-leak';
      assert.throws(() => validateMessage(name, changed), `${file}: ${field}`);
    }
  });
}

test('Results incoming CheckCompleted expectation matches Checking producer wire semantics', async () => {
  const producer = await json('contracts/events/checking/check-completed.v1.schema.json');
  const consumer = await json('contracts/events/results/consumers/check-completed.v1.schema.json');
  for (const schema of [producer, consumer]) { delete schema.$id; delete schema.title; delete schema.description; }
  assert.deepEqual(consumer, producer);
  assert.deepEqual(resultsCatalog.find(c => c.name === 'CheckCompleted').routes,
    checkingCatalog.find(c => c.name === 'CheckCompleted').routes);
});

test('Applied receipts preserve source/final publication and require a no-op receipt', () => {
  const noop = fixtures[0][2];
  const changed = fixtures[1][2];
  assert.equal(noop.payload.changed, false);
  assert.equal(noop.payload.basePublicationId, noop.payload.publicationId);
  assert.equal(changed.payload.changed, true);
  assert.notEqual(changed.payload.basePublicationId, changed.payload.publicationId);
  assert.equal(changed.payload.selectedReviewerId, 'jury-a');
  assert.equal(changed.payload.reviewerId, 'senior-1');
  assert.equal(changed.payload.evaluationDecisionOrigin, 'SeniorEscalation');
  assert.ok(changed.payload.escalationId);
  for (const field of ['basePublicationId', 'publicationId', 'selectedReviewerId', 'reviewerId', 'evaluationDecisionOrigin']) {
    const copy = structuredClone(noop); delete copy.payload[field];
    assert.throws(() => validateMessage('ResultEvaluationApplied', copy), `missing ${field}`);
  }
  const invalidSenior = structuredClone(changed); delete invalidSenior.payload.escalationId;
  assert.throws(() => validateMessage('ResultEvaluationApplied', invalidSenior));
  const invalidOrdinary = structuredClone(noop); invalidOrdinary.payload.escalationId = 'unexpected';
  assert.throws(() => validateMessage('ResultEvaluationApplied', invalidOrdinary));
});

test('Results receipt identity follows immutable evaluationId and conflict never carries scores', () => {
  for (const [name, , message] of fixtures)
    assert.throws(() => validateMessage(name, { ...message, aggregateId: 'another-evaluation' }));
  const rejected = fixtures[2][2];
  for (const field of ['score', 'previousScore', 'points', 'total']) {
    const changed = structuredClone(rejected); changed.payload[field] = '20';
    assert.throws(() => validateMessage('ResultEvaluationRejected', changed));
  }
  assert.equal(typeof rejected.payload.reasonCode, 'string');
  assert.ok(rejected.payload.reasonCode.length > 0, 'the final reason enum remains open in this approved contract');
});

test('Results and Checking catalogs agree with durable service-specific route bindings', async () => {
  const topology = await json('infra/compose/topology.json');
  for (const contract of [...checkingCatalog, ...resultsCatalog])
    for (const route of contract.routes) assert.ok(topology.bindings.some(binding => binding.key === route), `Missing binding ${route}`);
  assert.deepEqual(topology.bindings.filter(b => b.key === 'results.result-evaluation-applied.appeal.v1').map(b => b.queue), ['appeals.result-receipts.v1']);
  assert.deepEqual(topology.bindings.filter(b => b.key === 'results.result-evaluation-rejected.appeal.v1').map(b => b.queue), ['appeals.result-receipts.v1']);
  assert.equal(resultsCatalog.filter(c => c.direction === 'incoming').length, 1);
  assert.equal(resultsCatalog.filter(c => c.direction === 'outgoing').length, 2);
});

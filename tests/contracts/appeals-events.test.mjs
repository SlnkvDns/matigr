import { test } from 'node:test';
import assert from 'node:assert/strict';
import { appealsCatalog, checkingCatalog, resultsCatalog, schemas, json, validateMessage } from './support.mjs';

const fixtureCases = [
  ['AppealsRecheckRequested', 'appeals/recheck-requested.json'],
  ['AppealsCheckCompleted', 'appeals/consumers/checking/check-completed.appeal.json'],
  ['AppealsCheckCompleted', 'appeals/consumers/checking/check-completed.appeal-senior.json'],
  ['AppealsRecheckAssigned', 'appeals/consumers/checking/recheck-assigned.selected.json'],
  ['AppealsRecheckAssigned', 'appeals/consumers/checking/recheck-assigned.senior.json'],
  ['AppealsRecheckEscalated', 'appeals/consumers/checking/recheck-escalated.json'],
  ['AppealsRecheckRequestRejected', 'appeals/consumers/checking/recheck-request-rejected.busy.json'],
  ['AppealsResultEvaluationApplied', 'appeals/consumers/results/result-evaluation-applied.no-op.json'],
  ['AppealsResultEvaluationApplied', 'appeals/consumers/results/result-evaluation-applied.senior.json'],
  ['AppealsResultEvaluationRejected', 'appeals/consumers/results/result-evaluation-rejected.conflict.json'],
];
const messages = await Promise.all(fixtureCases.map(async ([name, file]) => [name, file, await json(`tests/contracts/fixtures/${file}`)]));

for (const [name, file, message] of messages) {
  test(`Appeals contract fixture validates: ${file}`, () => {
    assert.deepEqual(validateMessage(name, JSON.parse(JSON.stringify(message)), { producer: name === 'AppealsRecheckRequested' }), message);
  });
}

test('Appeals producer RecheckRequested equals Checking consumer expectation and derives reviewer from fixture subject', async () => {
  const producer = await json('contracts/events/appeals/recheck-requested.v1.schema.json');
  const consumer = await json('contracts/events/checking/consumers/recheck-requested.v1.schema.json');
  for (const schema of [producer, consumer]) { delete schema.$id; delete schema.title; delete schema.description; }
  assert.deepEqual(producer, consumer);
  const request = messages[0][2];
  const manifest = await json('infra/fixtures/manifest.json');
  const selectedSubject = manifest.subjects.find(subject => subject.testSubjectId === 'jury-a');
  assert.equal(request.payload.requestedReviewerId, selectedSubject.subjectId);
  assert.equal(request.aggregateId, request.payload.appealId);
  assert.equal(request.payload.recheckRequestId, 'request-1');
});

test('Appeals consumer expectations stay aligned with Checking and Results producer schemas', async () => {
  const checkingPairs = [
    ['contracts/events/checking/check-completed.v1.schema.json', 'contracts/events/appeals/consumers/check-completed.v1.schema.json'],
    ['contracts/events/checking/recheck-assigned.v1.schema.json', 'contracts/events/appeals/consumers/recheck-assigned.v1.schema.json'],
    ['contracts/events/checking/recheck-escalated.v1.schema.json', 'contracts/events/appeals/consumers/recheck-escalated.v1.schema.json'],
    ['contracts/events/checking/recheck-request-rejected.v1.schema.json', 'contracts/events/appeals/consumers/recheck-request-rejected.v1.schema.json'],
  ];
  for (const [source, target] of checkingPairs) {
    const provider = await json(source); const consumer = await json(target);
    delete provider.$id; delete provider.title; delete provider.description;
    delete consumer.$id; delete consumer.title; delete consumer.description;
    if (source.endsWith('check-completed.v1.schema.json')) {
      const restriction = consumer.allOf; delete consumer.allOf;
      assert.deepEqual(consumer, provider);
      assert.equal(restriction[0].properties.payload.properties.kind.const, 'Appeal');
    } else assert.deepEqual(consumer, provider);
  }
  for (const name of ['result-evaluation-applied', 'result-evaluation-rejected']) {
    const provider = await json(`contracts/events/results/${name}.v1.schema.json`);
    const consumer = await json(`contracts/events/appeals/consumers/${name}.v1.schema.json`);
    delete provider.$id; delete provider.title; delete provider.description;
    delete consumer.$id; delete consumer.title; delete consumer.description;
    assert.deepEqual(consumer, provider, `${name}: consumer semantics drifted from Results producer`);
  }
  assert.equal(appealsCatalog.filter(contract => contract.direction === 'incoming').length, 6);
  assert.equal(checkingCatalog.find(contract => contract.name === 'RecheckRequested').producer, 'Appeals');
  assert.equal(resultsCatalog.find(contract => contract.name === 'ResultEvaluationApplied').producer, 'Results');
});

test('CheckCompleted Appeals expectation accepts Appeal only, including SeniorEscalation lineage', async () => {
  const consumer = schemas.get('AppealsCheckCompleted');
  assert.equal(consumer.properties.payload.properties.kind.enum.includes('Initial'), true);
  const initial = await json('tests/contracts/fixtures/checking/check-completed.initial.json');
  assert.throws(() => validateMessage('AppealsCheckCompleted', initial));
  const selected = messages[1][2]; const senior = messages[2][2];
  assert.equal(selected.payload.reviewerId, selected.payload.selectedReviewerId);
  assert.notEqual(senior.payload.reviewerId, senior.payload.selectedReviewerId);
  assert.equal(senior.payload.decisionOrigin, 'SeniorEscalation');
});

test('all service contracts route to the correct durable queue without sharing competing consumers', async () => {
  const topology = await json('infra/compose/topology.json');
  for (const contract of [...checkingCatalog, ...resultsCatalog, ...appealsCatalog])
    for (const route of contract.routes) assert.ok(topology.bindings.some(binding => binding.key === route), `Missing route ${route}`);
  assert.deepEqual(topology.bindings.filter(b => b.key === 'appeals.recheck-requested.v1').map(b => b.queue), ['checking.recheck-requests.v1']);
  assert.deepEqual(topology.bindings.filter(b => b.key === 'checking.check-completed.appeal.v1').map(b => b.queue).sort(), ['appeals.checking-facts.v1', 'results.check-completed.v1']);
  assert.deepEqual(topology.bindings.filter(b => b.key === 'results.result-evaluation-applied.appeal.v1').map(b => b.queue), ['appeals.result-receipts.v1']);
  assert.deepEqual(topology.bindings.filter(b => b.key === 'results.result-evaluation-rejected.appeal.v1').map(b => b.queue), ['appeals.result-receipts.v1']);
});

test('requested basis is immutable across Checking and Results, while receipts distinguish source and final publication', () => {
  const request = messages[0][2].payload;
  const assigned = messages[3][2].payload;
  const assignedSenior = messages[4][2].payload;
  const escalated = messages[5][2].payload;
  const completed = messages[1][2].payload;
  const completedSenior = messages[2][2].payload;
  const appliedNoop = messages[7][2].payload;
  const appliedSenior = messages[8][2].payload;
  const rejected = messages[9][2].payload;
  for (const item of [assigned, assignedSenior, escalated, completed, completedSenior]) {
    for (const key of ['appealId', 'recheckRequestId', 'resultId', 'expectedResultVersion', 'tournamentId', 'teamId', 'taskId'])
      assert.equal(item[key], request[key], `${key} changed across Checking facts`);
  }
  for (const item of [completed, completedSenior]) {
    assert.equal(item.publicationId, request.publicationId);
    assert.equal(item.baseCheckId, request.baseCheckId);
    assert.equal(item.selectedReviewerId, request.requestedReviewerId);
  }
  for (const item of [appliedNoop, appliedSenior, rejected]) {
    for (const key of ['appealId', 'recheckRequestId', 'resultId', 'expectedResultVersion', 'tournamentId', 'teamId', 'taskId', 'baseCheckId', 'selectedReviewerId'])
      assert.equal(item[key], key === 'selectedReviewerId' ? request.requestedReviewerId : request[key], `${key} changed in Results receipt`);
  }
  assert.equal(appliedNoop.basePublicationId, request.publicationId);
  assert.equal(appliedNoop.publicationId, request.publicationId);
  assert.equal(appliedNoop.changed, false);
  assert.equal(appliedSenior.basePublicationId, request.publicationId);
  assert.notEqual(appliedSenior.publicationId, request.publicationId);
  assert.equal(appliedSenior.reviewerId, completedSenior.reviewerId);
  assert.equal(appliedSenior.assignmentId, completedSenior.assignmentId);
  assert.equal(appliedSenior.evaluationDecisionOrigin, completedSenior.decisionOrigin);
  assert.equal(appliedSenior.escalationId, completedSenior.escalationId);
  assert.equal(rejected.reasonCode, 'ResultVersionConflict');
});

test('Appeals consumer fixtures are independent copies of the producer-owned examples', async () => {
  const pairs = [
    ['checking/check-completed.appeal.json', 'appeals/consumers/checking/check-completed.appeal.json'],
    ['checking/check-completed.appeal-senior.json', 'appeals/consumers/checking/check-completed.appeal-senior.json'],
    ['checking/recheck-assigned.selected.json', 'appeals/consumers/checking/recheck-assigned.selected.json'],
    ['checking/recheck-assigned.senior.json', 'appeals/consumers/checking/recheck-assigned.senior.json'],
    ['checking/recheck-escalated.json', 'appeals/consumers/checking/recheck-escalated.json'],
    ['checking/recheck-request-rejected.busy.json', 'appeals/consumers/checking/recheck-request-rejected.busy.json'],
    ['results/result-evaluation-applied.no-op.json', 'appeals/consumers/results/result-evaluation-applied.no-op.json'],
    ['results/result-evaluation-applied.senior.json', 'appeals/consumers/results/result-evaluation-applied.senior.json'],
    ['results/result-evaluation-rejected.conflict.json', 'appeals/consumers/results/result-evaluation-rejected.conflict.json'],
  ];
  for (const [provider, consumer] of pairs)
    assert.deepEqual(await json(`tests/contracts/fixtures/${consumer}`), await json(`tests/contracts/fixtures/${provider}`), `${consumer}: stale fixture copy`);
});

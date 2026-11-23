import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkingCatalog, schemas, json, validateMessage } from './support.mjs';

const cases = [
  ['CheckCompleted', 'checking/check-completed.initial.json'],
  ['CheckCompleted', 'checking/check-completed.initial-senior.json'],
  ['CheckCompleted', 'checking/check-completed.appeal.json'],
  ['CheckCompleted', 'checking/check-completed.appeal-senior.json'],
  ['RecheckAssigned', 'checking/recheck-assigned.selected.json'],
  ['RecheckAssigned', 'checking/recheck-assigned.senior.json'],
  ['RecheckEscalated', 'checking/recheck-escalated.json'],
  ['RecheckRequestRejected', 'checking/recheck-request-rejected.busy.json'],
  ['RecheckRequested', 'appeals/recheck-requested.json'],
];
const fixtures = await Promise.all(cases.map(async ([name, file]) => [name, file, await json(`tests/contracts/fixtures/${file}`)]));
const initial = fixtures[0][2];
const appeal = fixtures[2][2];
const senior = fixtures[3][2];
const selected = fixtures[4][2];
const requested = fixtures[8][2];

for (const [name, file, message] of fixtures) {
  test(`positive wire roundtrip: ${file}`, () => {
    assert.deepEqual(validateMessage(name, JSON.parse(JSON.stringify(message)), { producer: true }), message);
  });
  test(`all required envelope/payload fields: ${file}`, () => {
    for (const key of schemas.get(name).required) {
      const changed = structuredClone(message);
      delete changed[key];
      assert.throws(() => validateMessage(name, changed), `${file}: missing envelope ${key}`);
    }
    for (const key of schemas.get(name).properties.payload.required) {
      const changed = structuredClone(message);
      delete changed.payload[key];
      assert.throws(() => validateMessage(name, changed), `${file}: missing payload ${key}`);
    }
  });
  test(`schema/producer/aggregate/UTC boundaries: ${file}`, () => {
    for (const [field, bad] of [['schemaVersion', 2], ['schemaVersion', '1'], ['producer', 'Untrusted'],
      ['messageType', 'matigr.unknown'], ['aggregateType', 'Wrong'], ['aggregateId', 'another'],
      ['aggregateVersion', 2], ['aggregateVersion', '0'], ['occurredAt', '2026-10-23T04:00:00+06:00'],
      ['occurredAt', 'invalid']]) {
      assert.throws(() => validateMessage(name, { ...message, [field]: bad }), `${file}: bad ${field}`);
    }
    for (const field of Object.keys(message.payload).filter(key => key.endsWith('At')))
      assert.throws(() => validateMessage(name, { ...message, payload: { ...message.payload, [field]: '2026-10-23T04:00:00+06:00' } }), `${file}: non-UTC ${field}`);
  });
  test(`no private materials, scores, capabilities, comments or requestId: ${file}`, () => {
    for (const key of ['capability', 'capabilityHash', 'capabilityCiphertext', 'assignmentToken', 'token', 'accessToken',
      'score', 'previousScore', 'points', 'total', 'scoreRuleVersion', 'comment', 'commentary', 'material',
      'materialRef', 'materialFile', 'materialUrl', 'requestId']) {
      for (const level of ['envelope', 'payload']) {
        const changed = structuredClone(message);
        if (level === 'envelope') changed[key] = 'must-not-leak';
        else changed.payload[key] = 'must-not-leak';
        assert.throws(() => validateMessage(name, changed), `${file}: ${level}.${key}`);
      }
    }
  });
}

test('Initial completion has expected=0, no appeal basis, and compatible origin', () => {
  for (const field of ['appealId', 'recheckRequestId', 'selectedReviewerId', 'resultId', 'baseCheckId', 'publicationId']) {
    assert.throws(() => validateMessage('CheckCompleted', { ...initial, payload: { ...initial.payload, [field]: 'unexpected' } }));
  }
  for (const update of [{ expectedResultVersion: '1' }, { decisionOrigin: 'AppealReview' }, { outcome: 'Partial' }, { kind: 'Unknown' },
    { expectedResultVersion: 0 }, { reviewRevision: '0' }, { escalationId: 'unjustified' }]) {
    assert.throws(() => validateMessage('CheckCompleted', { ...initial, payload: { ...initial.payload, ...update } }));
  }
});
test('Appeal completion requires the full immutable basis, positive version and selected reviewer', () => {
  for (const field of ['appealId', 'recheckRequestId', 'selectedReviewerId', 'resultId', 'baseCheckId', 'publicationId']) {
    const changed = structuredClone(appeal);
    delete changed.payload[field];
    assert.throws(() => validateMessage('CheckCompleted', changed));
  }
  for (const update of [{ expectedResultVersion: '0' }, { decisionOrigin: 'OrdinaryReview' },
    { reviewerId: 'someone-else' }, { expectedResultVersion: 1 }, { expectedResultVersion: '01' }]) {
    assert.throws(() => validateMessage('CheckCompleted', { ...appeal, payload: { ...appeal.payload, ...update } }));
  }
});
test('both senior completion kinds and senior assignment require escalationId', () => {
  for (const index of [1, 3, 5]) {
    const [name, , changed] = structuredClone(fixtures[index]);
    delete changed.payload.escalationId;
    assert.throws(() => validateMessage(name, changed));
  }
  assert.notEqual(senior.payload.reviewerId, senior.payload.selectedReviewerId);
  validateMessage('CheckCompleted', senior);
});
test('Selected assignment must belong to the selected reviewer and cannot invent escalation', () => {
  for (const update of [{ reviewerId: 'different-reviewer' }, { escalationId: 'unexpected' }])
    assert.throws(() => validateMessage('RecheckAssigned', { ...selected, payload: { ...selected.payload, ...update } }));
});
test('RecheckEscalated releases the same selected reviewer and carries the same Check/attempt', () => {
  const message = fixtures[6][2];
  assert.throws(() => validateMessage('RecheckEscalated', { ...message, payload: { ...message.payload, releasedReviewerId: 'other' } }));
  assert.equal(message.payload.checkId, selected.payload.checkId);
  assert.equal(message.payload.recheckRequestId, selected.payload.recheckRequestId);
  assert.equal(message.payload.appealId, selected.payload.appealId);
  assert.equal(message.payload.escalationId, senior.payload.escalationId);
});
test('ReviewerBusy has no created Check or assignment and immutable receipt version=1', () => {
  const message = fixtures[7][2];
  for (const field of ['checkId', 'assignmentId', 'reviewerId', 'escalationId'])
    assert.throws(() => validateMessage('RecheckRequestRejected', { ...message, payload: { ...message.payload, [field]: 'not-created' } }));
  assert.throws(() => validateMessage('RecheckRequestRejected', { ...message, aggregateVersion: '2' }));
  assert.throws(() => validateMessage('RecheckRequestRejected', { ...message, payload: { ...message.payload, reasonCode: 'DatabaseDown' } }));
});
test('all fixed rejection codes are parseable; technical failure is not a business receipt', () => {
  const message = fixtures[7][2];
  for (const reasonCode of ['ReviewerBusy', 'MaterialNotFound', 'BasisMismatch', 'BaseNotAppealable', 'RequestConflict', 'ReviewerNotAuthorized'])
    validateMessage('RecheckRequestRejected', { ...message, payload: { ...message.payload, reasonCode } });
});
test('expectedResultVersion/reviewRevision/aggregateVersion retain precision above JS safe integer', () => {
  const large = '9007199254740993';
  const changed = { ...appeal, aggregateVersion: large, payload: { ...appeal.payload, expectedResultVersion: large, reviewRevision: large } };
  const roundtrip = JSON.parse(JSON.stringify(changed));
  validateMessage('CheckCompleted', roundtrip);
  assert.equal(BigInt(roundtrip.aggregateVersion), 9007199254740993n);
  assert.equal(BigInt(roundtrip.payload.expectedResultVersion), 9007199254740993n);
});
test('original reviewer is valid in requested recheck and full basis is unchanged on completion', () => {
  validateMessage('RecheckRequested', requested);
  assert.equal(requested.payload.requestedReviewerId, initial.payload.reviewerId);
  assert.equal(requested.payload.baseCheckId, initial.payload.checkId);
  assert.equal(requested.payload.baseOutcome, initial.payload.outcome);
  assert.equal(appeal.payload.selectedReviewerId, requested.payload.requestedReviewerId);
  assert.equal(selected.payload.selectedReviewerId, requested.payload.requestedReviewerId);
  assert.equal(senior.payload.selectedReviewerId, requested.payload.requestedReviewerId);
  for (const key of ['solutionId', 'materialVersionId', 'tournamentId', 'teamId', 'taskId', 'appealId', 'recheckRequestId',
    'resultId', 'expectedResultVersion', 'publicationId', 'baseCheckId']) {
    assert.equal(appeal.payload[key], requested.payload[key]);
    assert.equal(senior.payload[key], requested.payload[key]);
  }
});
test('readers tolerate optional additive fields while producer output rejects undeclared fields', () => {
  const extended = { ...initial, futureEnvelopeHint: 'optional', payload: { ...initial.payload, futureOptionalField: 'optional' } };
  validateMessage('CheckCompleted', extended);
  assert.throws(() => validateMessage('CheckCompleted', extended, { producer: true }));
});
test('published routing agrees with actual Compose bindings and separate completion queues', async () => {
  const topology = await json('infra/compose/topology.json');
  for (const contract of checkingCatalog) {
    for (const route of contract.routes) assert.ok(topology.bindings.some(b => b.key === route), `Missing binding ${route}`);
  }
  const initialQueues = topology.bindings.filter(b => b.key === 'checking.check-completed.initial.v1').map(b => b.queue);
  const appealQueues = topology.bindings.filter(b => b.key === 'checking.check-completed.appeal.v1').map(b => b.queue);
  assert.deepEqual(initialQueues, ['results.check-completed.v1']);
  assert.deepEqual(new Set(appealQueues), new Set(['results.check-completed.v1', 'appeals.checking-facts.v1']));
  assert.equal(checkingCatalog.filter(c => c.direction === 'outgoing').length, 4);
  assert.equal(checkingCatalog.filter(c => c.direction === 'incoming').length, 1);
  assert.equal(checkingCatalog.find(c => c.name === 'RecheckRequested').producer, 'Appeals');
});

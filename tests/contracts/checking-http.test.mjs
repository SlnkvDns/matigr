import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openapi, validateHttp, resolveObject, json } from './support.mjs';

const prefix = '/api/v1/checking';
const operations = Object.entries(openapi.paths).flatMap(([path, item]) => Object.entries(item)
  .filter(([method]) => ['get', 'post'].includes(method)).map(([method, operation]) => ({ path, method, operation })));
const fixture = (await json('tests/contracts/fixtures/checking/check-completed.initial.json')).payload;
const check = Object.fromEntries('checkId solutionId materialVersionId tournamentId teamId taskId kind reviewRevision'.split(' ').map(k => [k, fixture[k]]));
Object.assign(check, { state: 'Assigned', version: '2', createdAt: '2026-10-23T04:00:00Z' });
const assignment = { check, assignmentId: 'assignment-1', reviewerId: fixture.reviewerId,
  startedAt: '2026-10-23T04:01:00Z', capability: 'test-secret-only-in-protected-http' };
const params = operation => (operation.parameters ?? []).map(resolveObject);

test('OpenAPI 3.1 validates and exposes exactly the approved Checking surface plus health', () => {
  assert.equal(openapi.openapi, '3.1.1');
  assert.deepEqual(new Set(operations.map(o => `${o.method} ${o.path}`)), new Set([
    `get ${prefix}/queues`, `post ${prefix}/me/assignments/next`, `get ${prefix}/me/assignment`,
    `get ${prefix}/checks/{checkId}`, `get ${prefix}/checks/{checkId}/material`,
    `post ${prefix}/checks/{checkId}/complete`, `post ${prefix}/checks/{checkId}/refuse`,
    'post /dev/v1/session/select', 'get /health/live', 'get /health/ready',
  ]));
  assert.equal(new Set(operations.map(o => o.operation.operationId)).size, operations.length);
  assert.ok(!JSON.stringify(openapi.paths).includes('/upload'));
  assert.deepEqual(openapi.security, [{ UserBearer: [] }]);
  assert.equal(openapi.components.securitySchemes.UserBearer.scheme, 'bearer');
});
test('private operations preserve bearer security and no-store on every declared response', () => {
  for (const { path, operation } of operations.filter(o => o.path.startsWith(prefix))) {
    assert.equal(operation.security, undefined, `${path} must inherit bearer requirement`);
    assert.ok(operation['x-permission']);
    assert.ok(operation.responses['401'] && operation.responses['403']);
    for (const response of Object.values(operation.responses)) {
      const cache = resolveObject(resolveObject(response).headers['Cache-Control']);
      assert.equal(cache.schema.const, 'private, no-store');
    }
  }
});
test('business mutations require idempotency; known-Check actions require If-Match and conflict statuses', () => {
  for (const action of ['complete', 'refuse']) {
    const operation = openapi.paths[`${prefix}/checks/{checkId}/${action}`].post;
    for (const name of ['Idempotency-Key', 'If-Match']) assert.ok(params(operation).some(p => p.name === name && p.required));
    for (const status of ['400', '401', '403', '404', '409', '412', '428', '503']) assert.ok(operation.responses[status]);
    assert.ok(resolveObject(operation.responses['200']).headers.ETag);
    assert.match(params(operation).find(p => p.name === 'Idempotency-Key').description, /before evaluating If-Match/);
  }
  const next = openapi.paths[`${prefix}/me/assignments/next`].post;
  assert.ok(params(next).some(p => p.name === 'Idempotency-Key' && p.required));
  assert.ok(!params(next).some(p => p.name === 'If-Match'));
  assert.ok(next.responses['204'] && next.responses['409']);
  assert.ok(openapi.paths[`${prefix}/me/assignment`].get.responses['204']);
});
test('GET aggregate and current assignment return strong ETags; current assignment cannot be filtered away', () => {
  for (const path of [`${prefix}/checks/{checkId}`, `${prefix}/me/assignment`])
    assert.ok(resolveObject(openapi.paths[path].get.responses['200']).headers.ETag);
  assert.deepEqual(params(openapi.paths[`${prefix}/me/assignment`].get), []);
  validateHttp('ETag', '"9007199254740993"');
  for (const invalid of ['W/"2"', '*', '"1", "2"', '2', '"0"', '"01"']) assert.throws(() => validateHttp('ETag', invalid));
});
test('completion and refusal accept no browser identity, comments, scores or expectedVersion body shortcuts', () => {
  const complete = { assignmentId: 'assignment-1', capability: 'secret', outcome: 'Incorrect' };
  const refuse = { assignmentId: 'assignment-1', capability: 'secret' };
  validateHttp('CompleteRequest', complete);
  validateHttp('RefuseRequest', refuse);
  for (const [name, body] of [['CompleteRequest', complete], ['RefuseRequest', refuse]]) {
    for (const property of ['reviewerId', 'teamId', 'role', 'comment', 'score', 'expectedVersion', 'materialRef'])
      assert.throws(() => validateHttp(name, { ...body, [property]: 'injected' }));
    for (const property of Object.keys(body)) {
      const missing = { ...body }; delete missing[property];
      assert.throws(() => validateHttp(name, missing));
    }
  }
  assert.throws(() => validateHttp('CompleteRequest', { ...complete, outcome: 'Partial' }));
});
test('assignment capability is restricted to owner-only DTO, not a queue or ordinary Check snapshot', () => {
  validateHttp('MyAssignment', assignment);
  validateHttp('CheckView', check);
  const page = { tournamentId: check.tournamentId, queue: 'general', items: [], nextCursor: null };
  validateHttp('QueuePage', page);
  for (const property of ['capability', 'assignmentToken', 'materialRef', 'score', 'comment']) {
    assert.throws(() => validateHttp('CheckView', { ...check, [property]: 'private' }));
    assert.throws(() => validateHttp('QueuePage', { ...page, [property]: 'private' }));
  }
  assert.throws(() => validateHttp('MyAssignment', { ...assignment, check: { ...check, state: 'Completed', outcome: 'Correct', decisionOrigin: 'OrdinaryReview' } }));
  assert.throws(() => validateHttp('MyAssignment', { ...assignment, capability: '' }));
});
test('Completed and SeniorQueued transition DTOs distinguish saved verdict from escalation', () => {
  const completed = { ...check, state: 'Completed', version: '3', outcome: 'Incorrect', decisionOrigin: 'OrdinaryReview' };
  const queued = { ...check, state: 'SeniorQueued', version: '3' };
  validateHttp('CompletedCheckView', completed);
  validateHttp('SeniorQueuedCheckView', queued);
  assert.throws(() => validateHttp('CompletedCheckView', { ...check, state: 'Completed' }));
  assert.throws(() => validateHttp('CompletedCheckView', queued));
  assert.throws(() => validateHttp('SeniorQueuedCheckView', completed));
  assert.throws(() => validateHttp('CheckView', { ...queued, outcome: 'Incorrect' }));
  assert.throws(() => validateHttp('CheckView', { ...completed, decisionOrigin: 'AppealReview' }));
  validateHttp('CompletedCheckView', { ...completed, kind: 'Appeal', decisionOrigin: 'SeniorEscalation' });
});
test('take-next and queue DTOs reject unknown queues and client-selected reviewer', () => {
  validateHttp('TakeNextRequest', { tournamentId: check.tournamentId, queue: 'general' });
  validateHttp('TakeNextRequest', { tournamentId: check.tournamentId, queue: 'senior' });
  assert.throws(() => validateHttp('TakeNextRequest', { tournamentId: check.tournamentId, queue: 'appeals' }));
  assert.throws(() => validateHttp('TakeNextRequest', { tournamentId: check.tournamentId, queue: 'general', reviewerId: 'other' }));
});
test('material response is protected read-only data with 503 dependency failure, not a public URL', () => {
  const operation = openapi.paths[`${prefix}/checks/{checkId}/material`].get;
  const response = resolveObject(operation.responses['200']);
  assert.deepEqual(Object.keys(response.content), ['text/plain']);
  assert.ok(operation.responses['503']);
  assert.equal(params(operation).length, 1);
  assert.equal(params(operation)[0].name, 'checkId');
  assert.match(operation['x-permission'], /current assignment owner/);
});
test('dev selection accepts fixture key only and returns server-selected stable profile', async () => {
  const manifest = await json('infra/fixtures/manifest.json');
  validateHttp('SessionSelectRequest', { testSubjectId: manifest.subjects[0].testSubjectId });
  for (const property of ['subjectId', 'roles', 'role', 'teamId', 'tournamentIds'])
    assert.throws(() => validateHttp('SessionSelectRequest', { testSubjectId: 'jury-a', [property]: 'injected' }));
  for (const subject of manifest.subjects) {
    const { testSubjectId, ...profile } = subject;
    validateHttp('DevSession', { accessToken: 'not-a-real-token', tokenType: 'Bearer', expiresAt: '2026-10-23T05:00:00Z', profile });
    if (profile.roles.includes('Team')) {
      const missingTeam = { ...profile }; delete missingTeam.teamId;
      assert.throws(() => validateHttp('DevProfile', missingTeam));
    } else assert.throws(() => validateHttp('DevProfile', { ...profile, teamId: 'unauthorized' }));
  }
  const operation = openapi.paths['/dev/v1/session/select'].post;
  assert.deepEqual(operation.security, []);
  assert.match(operation.description, /Disabled \(404\) outside Local\/Test/);
});
test('ProblemDetails requires machine-readable code/correlationId and all examples match HTTP status', () => {
  for (const response of Object.values(openapi.components.responses)) {
    for (const { value } of Object.values(response.content['application/problem+json'].examples)) {
      validateHttp('ProblemDetails', value);
      assert.match(response.description, new RegExp(`^${value.status}:`));
    }
  }
  const valid = { type: 'about:blank', title: 'Conflict', status: 409, code: 'IdempotencyConflict', correlationId: 'request-1' };
  for (const property of ['code', 'correlationId']) {
    const missing = { ...valid }; delete missing[property];
    assert.throws(() => validateHttp('ProblemDetails', missing));
  }
  assert.throws(() => validateHttp('ProblemDetails', { ...valid, capability: 'secret' }));
});
test('HTTP versions remain strings beyond JavaScript precision', () => {
  validateHttp('CheckView', { ...check, version: '9007199254740993', reviewRevision: '9007199254740994' });
  assert.throws(() => validateHttp('CheckView', { ...check, version: 9007199254740992 }));
});
test('implemented health response DTOs have explicit anonymous access and readiness 503', () => {
  validateHttp('Liveness', { status: 'Alive' });
  validateHttp('Readiness', { status: 'Ready', database: true, broker: true });
  validateHttp('Readiness', { status: 'Unavailable', database: false, broker: true });
  assert.deepEqual(openapi.paths['/health/live'].get.security, []);
  assert.deepEqual(openapi.paths['/health/ready'].get.security, []);
  assert.ok(openapi.paths['/health/ready'].get.responses['503']);
});

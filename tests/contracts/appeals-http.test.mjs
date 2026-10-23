import { test } from 'node:test';
import assert from 'node:assert/strict';
import { appealsOpenapi, resultsOpenapi, validateAppealsHttp, validateAppealBasis, resolveAppealsObject, resolveResultsObject, json } from './support.mjs';

const paths = appealsOpenapi.paths;
const operations = Object.entries(paths).flatMap(([path, item]) => Object.entries(item)
  .filter(([method]) => ['get', 'post', 'put', 'patch', 'delete'].includes(method)).map(([method, operation]) => ({ path, method, operation })));

test('Appeals OpenAPI exposes only approved user flows, local end fact and health', () => {
  assert.equal(appealsOpenapi.openapi, '3.1.1');
  assert.deepEqual(new Set(operations.map(o => `${o.method} ${o.path}`)), new Set([
    'post /api/v1/appeals', 'get /api/v1/appeals/me', 'get /api/v1/appeals/tournaments/{tournamentId}',
    'get /api/v1/appeals/{appealId}', 'post /api/v1/appeals/{appealId}/claim',
    'post /dev/v1/tournaments/{tournamentId}/end-facts', 'get /health/live', 'get /health/ready',
  ]));
  assert.equal(new Set(operations.map(o => o.operation.operationId)).size, operations.length);
  assert.ok(!JSON.stringify(paths).match(/\/(upload|withdraw|revoke|admin|tournaments\/\{[^}]+\}\/close)$/i));
  assert.deepEqual(appealsOpenapi.security, []);
  for (const { path, operation } of operations.filter(o => pathIsProtected(o.path))) {
    assert.deepEqual(operation.security, [{ UserBearer: [] }], `${path}: explicit user identity required`);
    assert.ok(operation['x-permission'], `${path}: server-side permission documented`);
  }
  assert.deepEqual(paths['/health/live'].get.security, []);
  assert.deepEqual(paths['/health/ready'].get.security, []);
  assert.match(paths['/dev/v1/tournaments/{tournamentId}/end-facts'].post.description, /absent outside Local\/Test/);
  assert.match(paths['/dev/v1/tournaments/{tournamentId}/end-facts'].post.description, /does not provide tournament administration/);
});

function pathIsProtected(path) { return !path.startsWith('/health/'); }

test('submit trusts only the authenticated team and current Results basis', async () => {
  const operation = paths['/api/v1/appeals'].post;
  const body = { resultId: 'result-1', expectedResultVersion: '1' };
  validateAppealsHttp('SubmitAppealRequest', body);
  for (const field of ['teamId', 'reviewerId', 'role', 'publicationId', 'outcome', 'material', 'score'])
    assert.throws(() => validateAppealsHttp('SubmitAppealRequest', { ...body, [field]: 'client-value' }));
  assert.ok(operation.parameters.some(p => p.name === 'Idempotency-Key' && p.required));
  assert.ok(operation.responses['201'] && operation.responses['409'] && operation.responses['503']);
  assert.match(operation.description, /does not enqueue RecheckRequested/);
  assert.match(operation.description, /server-side subject/);
  const error = resolveAppealsObject(operation.responses['409']);
  assert.match(error.description, /AlreadyAppealed/);
  assert.match(error.description, /BaseNotAppealable/);
  assert.match(error.description, /ResultVersionConflict/);
});

test('manual claim is versioned/idempotent and 202 is only pending Checking confirmation', () => {
  const operation = paths['/api/v1/appeals/{appealId}/claim'].post;
  assert.ok(operation.parameters.some(p => p.name === 'If-Match' && p.required));
  assert.ok(operation.parameters.some(p => p.name === 'Idempotency-Key' && p.required));
  assert.ok(operation.responses['202'] && operation.responses['412'] && operation.responses['428']);
  validateAppealsHttp('ClaimAccepted', { appealId: 'appeal-1', state: 'AcceptedAwaitingRecheck', version: '2' });
  assert.throws(() => validateAppealsHttp('ClaimAccepted', { appealId: 'appeal-1', state: 'RecheckPending', version: '2' }));
  assert.throws(() => validateAppealsHttp('ClaimAccepted', { appealId: 'appeal-1', state: 'AcceptedAwaitingRecheck', version: '2', reviewerId: 'client' }));
  assert.match(operation.description, /server-derived/);
  assert.match(operation.description, /not a confirmed Checking assignment/);
});

test('private appeal views and queue never expose raw receipts, capability or solution material', () => {
  const view = { appealId: 'appeal-1', tournamentId: 't-1', teamId: 'team-1', taskId: 'task-1', state: 'Registered', version: '1', resultId: 'r-1', resultVersion: '1', baseCheckId: 'check-1', basePublicationId: 'pub-1', baseOutcome: 'Correct', submittedAt: '2026-10-23T04:00:00Z' };
  validateAppealsHttp('AppealView', view);
  for (const field of ['receipt', 'rawPayload', 'capability', 'material', 'comment', 'score'])
    assert.throws(() => validateAppealsHttp('AppealView', { ...view, [field]: 'private' }));
  const queue = { tournamentId: 't-1', items: [{ appealId: 'appeal-1', tournamentId: 't-1', teamId: 'team-1', taskId: 'task-1', state: 'Registered', submittedAt: '2026-10-23T04:00:00Z', version: '1' }] };
  validateAppealsHttp('AppealQueue', queue);
  assert.throws(() => validateAppealsHttp('AppealQueue', { ...queue, items: [{ ...queue.items[0], solutionId: 'private-material-link' }] }));
  for (const { path, operation } of operations.filter(o => !pathIsPublic(o.path))) {
    for (const response of Object.values(operation.responses)) {
      const resolved = resolveAppealsObject(response);
      if (resolved.content?.['application/problem+json'] || response.content?.['application/problem+json'])
        assert.ok(resolveAppealsObject(response).headers?.['Cache-Control'], `${path}: private error must be no-store`);
    }
  }
});

function pathIsPublic(path) { return path.startsWith('/health/'); }

test('Appeals basis consumer schema and fixture match Results provider schema and RecheckRequested immutable basis', async () => {
  const consumer = await json('contracts/http/appeals/results-appeal-basis.v1.schema.json');
  const fixture = await json('tests/contracts/fixtures/appeals/results-appeal-basis.json');
  validateAppealBasis(fixture);
  const request = await json('tests/contracts/fixtures/appeals/recheck-requested.json');
  for (const [consumerField, requestField] of [['resultId', 'resultId'], ['resultVersion', 'expectedResultVersion'], ['tournamentId', 'tournamentId'], ['teamId', 'teamId'], ['taskId', 'taskId'], ['solutionId', 'solutionId'], ['materialVersionId', 'materialVersionId'], ['publicationId', 'publicationId'], ['sourceCheckId', 'baseCheckId'], ['outcome', 'baseOutcome']])
    assert.equal(fixture[consumerField], request.payload[requestField], `${consumerField} changed between trusted basis and event`);
  const basisSchema = resolveResultsObject({ $ref: '#/components/schemas/AppealBasis' });
  assert.deepEqual(Object.keys(consumer.properties).sort(), Object.keys(basisSchema.properties).sort());
  assert.deepEqual(consumer.required.sort(), basisSchema.required.sort());
  assert.equal(consumer.additionalProperties, basisSchema.additionalProperties);
  assert.deepEqual(resultsOpenapi.components.schemas.AppealBasis, basisSchema);
  for (const field of ['material', 'reviewerId', 'assignmentToken', 'appealAllowed'])
    assert.throws(() => validateAppealBasis({ ...fixture, [field]: 'forbidden' }));
  assert.ok(paths['/api/v1/appeals'].post.responses['503'], 'basis/Results outage does not silently consume quota');
});

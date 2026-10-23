import { test } from 'node:test';
import assert from 'node:assert/strict';
import { resultsOpenapi, validateResultsHttp, resolveResultsObject, json } from './support.mjs';

const paths = resultsOpenapi.paths;
const ops = Object.entries(paths).flatMap(([path, item]) => Object.entries(item)
  .filter(([method]) => ['get', 'post', 'put', 'patch', 'delete'].includes(method)).map(([method, operation]) => ({ path, method, operation })));
const apiPrefix = '/api/v1/results';

test('Results OpenAPI 3.1 has exactly the approved public/private/internal read surface and health', () => {
  assert.equal(resultsOpenapi.openapi, '3.1.1');
  assert.deepEqual(new Set(ops.map(o => `${o.method} ${o.path}`)), new Set([
    `get ${apiPrefix}/tournaments/{tournamentId}/standings`,
    `get ${apiPrefix}/tournaments/{tournamentId}/standings/events`,
    `get ${apiPrefix}/me/tournaments/{tournamentId}/results`,
    'get /internal/v1/results/{resultId}/appeal-basis',
    'get /health/live', 'get /health/ready',
  ]));
  assert.equal(new Set(ops.map(o => o.operation.operationId)).size, ops.length);
  assert.ok(!JSON.stringify(paths).match(/\/(upload|submissions|tournaments\/\{[^}]+\}\/admin)/i));
  for (const path of [`${apiPrefix}/tournaments/{tournamentId}/standings`, `${apiPrefix}/tournaments/{tournamentId}/standings/events`])
    assert.deepEqual(paths[path].get.security, []);
  assert.deepEqual(paths[`${apiPrefix}/me/tournaments/{tournamentId}/results`].get.security, [{ UserBearer: [] }]);
  assert.deepEqual(paths['/internal/v1/results/{resultId}/appeal-basis'].get.security, [{ ServiceBearer: [] }]);
  assert.match(resultsOpenapi.components.securitySchemes.ServiceBearer.description, /User JWT is not valid here/i);
});

test('public snapshots distinguish pending cells, published scores and do not expose private fields', () => {
  const valid = { tournamentId: 't-1', snapshotVersion: '9007199254740993', tasks: [{ taskId: 'task-1', name: 'A' }], teams: [
    { teamId: 'team-a', name: 'A', cells: [{ status: 'Published', score: '10' }], total: '10', rank: 1 },
    { teamId: 'team-b', name: 'B', cells: [{ status: 'Pending' }], total: '0', rank: 1 },
  ] };
  validateResultsHttp('StandingsSnapshot', valid);
  validateResultsHttp('StandingsSnapshot', { ...valid, snapshotVersion: '0' });
  for (const bad of [
    { ...valid, teams: [{ ...valid.teams[0], cells: [{ status: 'Pending', score: '0' }] }] },
    { ...valid, teams: [{ ...valid.teams[0], cells: [{ status: 'Published' }] }] },
    { ...valid, teams: [{ ...valid.teams[0], reviewerId: 'private' }] },
    { ...valid, teams: [{ ...valid.teams[0], cells: [{ status: 'Incorrect', score: '0' }] }] },
    { ...valid, snapshotVersion: 9007199254740993 },
  ]) assert.throws(() => validateResultsHttp('StandingsSnapshot', bad));
  const operation = paths[`${apiPrefix}/tournaments/{tournamentId}/standings`].get;
  assert.ok(resolveResultsObject(operation.responses['200']).headers.ETag);
  assert.match(operation.description, /Results owns totals and places/);
});

test('private results do not accept caller-supplied team identity and hint is not authority', () => {
  validateResultsHttp('MyTournamentResults', { tournamentId: 't-1', results: [{ taskId: 'task-1', resultId: 'r-1', resultVersion: '1', publicationId: 'p-1', outcome: 'Correct', score: '20', appealAvailabilityHint: 'Potentially available' }] });
  assert.throws(() => validateResultsHttp('MyTournamentResults', { tournamentId: 't-1', teamId: 'other', results: [] }));
  assert.match(paths[`${apiPrefix}/me/tournaments/{tournamentId}/results`].get.description, /Appeals alone decides/);
  assert.ok(paths[`${apiPrefix}/me/tournaments/{tournamentId}/results`].get.responses['401']);
  assert.ok(paths[`${apiPrefix}/me/tournaments/{tournamentId}/results`].get.responses['403']);
});

test('internal basis is the canonical published appeal basis and keeps version errors explicit', () => {
  const valid = { resultId: 'r-1', resultVersion: '2', tournamentId: 't-1', teamId: 'team-1', taskId: 'task-1', solutionId: 's-1', materialVersionId: 'm-1', sourceCheckId: 'c-1', decisionOrigin: 'OrdinaryReview', outcome: 'Correct', scoreRuleVersion: 'fixture-v1', publicationId: 'p-2', publishedAt: '2026-10-23T05:00:00Z', publicationState: 'Published' };
  validateResultsHttp('AppealBasis', valid);
  for (const field of ['material', 'assignmentToken', 'appealAllowed', 'reviewerId'])
    assert.throws(() => validateResultsHttp('AppealBasis', { ...valid, [field]: 'private' }));
  assert.equal(paths['/internal/v1/results/{resultId}/appeal-basis'].get.parameters.find(p => p.name === 'expectedResultVersion').required, true);
  assert.match(paths['/internal/v1/results/{resultId}/appeal-basis'].get['x-permission'], /user JWT is not accepted/);
  assert.ok(paths['/internal/v1/results/{resultId}/appeal-basis'].get.responses['404']);
  assert.ok(paths['/internal/v1/results/{resultId}/appeal-basis'].get.responses['409']);
  const unavailable = resolveResultsObject(paths['/internal/v1/results/{resultId}/appeal-basis'].get.responses['409']);
  assert.match(unavailable.description, /not rebased/);
});

test('Results JSON examples and API schemas preserve exact integer-string forms', async () => {
  const baseline = await json('infra/fixtures/manifest.json');
  assert.ok(baseline.teams.length > 0);
  assert.throws(() => validateResultsHttp('Score', 10));
  assert.throws(() => validateResultsHttp('Score', '10.0'));
  assert.throws(() => validateResultsHttp('PositiveVersion', '01'));
  assert.throws(() => validateResultsHttp('AppealBasis', { resultId: 'r', resultVersion: '1', tournamentId: 't', teamId: 'team', taskId: 'task', solutionId: 's', materialVersionId: 'm', sourceCheckId: 'c', decisionOrigin: 'OrdinaryReview', outcome: 'Correct', scoreRuleVersion: 'v1', publicationId: 'p', publishedAt: '2026-10-23T05:00:00+06:00', publicationState: 'Published' }));
});

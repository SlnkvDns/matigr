import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

// Uses real HTTP/identity/PostgreSQL. Requires fresh fixture assignments; never resets data.
const root = path.resolve(import.meta.dirname, '..');
assert.ok(process.env.COMPOSE_PROJECT_NAME, 'Select an isolated/fresh Compose project explicitly');
const base = 'http://127.0.0.1:5101';
const api = '/api/v1/checking';
const manifest = JSON.parse(await readFile(path.join(root, 'infra/fixtures/manifest.json'), 'utf8'));
const tournamentId = manifest.tournament.tournamentId;
const spec = JSON.parse(await readFile(path.join(root, 'contracts/http/checking/openapi.json'), 'utf8'));
const ajv = new Ajv2020({ strict: false, allErrors: true });
addFormats(ajv);
const validators = Object.fromEntries(['QueuePage', 'MyAssignment', 'CheckView', 'ProblemDetails'].map(name =>
  [name, ajv.compile({ $ref: `#/components/schemas/${name}`, components: spec.components })]));
function schema(name, value) { assert.ok(validators[name](value), `${name}: ${ajv.errorsText(validators[name].errors)}`); }
function samePrivate(a, b) {
  const hash = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');
  assert.ok(hash(a) === hash(b), 'Owner response must be stable; sensitive values omitted');
}
async function select(testSubjectId) {
  const response = await fetch(base + '/dev/v1/session/select', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ testSubjectId }) });
  assert.equal(response.status, 200);
  return (await response.json()).accessToken;
}
async function request(url, token, body, key) {
  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (key !== undefined) headers['Idempotency-Key'] = key;
  const response = await fetch(base + api + url, { method: body === undefined ? 'GET' : 'POST', headers,
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(15000) });
  const cache = response.headers.get('cache-control') ?? '';
  assert.ok(cache.includes('private') && cache.includes('no-store'));
  if (response.status === 204) { assert.equal(await response.text(), ''); return { status: 204 }; }
  if (response.headers.get('content-type')?.startsWith('text/plain'))
    return { status: response.status, bytes: Buffer.from(await response.arrayBuffer()) };
  const value = await response.json();
  if (response.status >= 400) schema('ProblemDetails', value);
  return { status: response.status, value, etag: response.headers.get('etag') };
}
const a = await select('jury-a'), b = await select('jury-b'), senior = await select('senior'), team = await select('team-alpha');
for (const token of [a, b, senior]) assert.equal((await request('/me/assignment', token)).status, 204, 'Fresh assignments required');
assert.equal((await request('/queues?tournamentId=' + tournamentId + '&queue=general')).status, 401);
assert.equal((await request('/queues?tournamentId=' + tournamentId + '&queue=general', team)).status, 403);
const initial = await request('/queues?tournamentId=' + tournamentId + '&queue=general', a);
schema('QueuePage', initial.value); assert.equal(initial.value.items.length, 6);
assert.ok(!JSON.stringify(initial.value).includes('capability'));
const next = '/me/assignments/next'; const general = { tournamentId, queue: 'general' }; const seniorQueue = { tournamentId, queue: 'senior' };
assert.equal((await request(next, a, general)).status, 400);
assert.equal((await request(next, a, { ...general, reviewerId: 'browser-authority' }, 'bad-body')).status, 400);
assert.equal((await request(next, a, seniorQueue, 'unauthorized-queue')).status, 403);
const race = await Promise.all([request(next, a, general, 'lost-reply'), request(next, a, general, 'lost-reply')]);
for (const response of race) { assert.equal(response.status, 200); schema('MyAssignment', response.value); }
samePrivate(race[0].value, race[1].value);
assert.equal(race[0].etag, '"' + race[0].value.check.version + '"');
const second = await request(next, b, general, 'lost-reply');
assert.equal(second.status, 200); schema('MyAssignment', second.value);
assert.notEqual(second.value.check.checkId, race[0].value.check.checkId);
const busy = await request(next, a, general, 'other-tab');
assert.equal(busy.status, 409); assert.equal(busy.value.code, 'ReviewerBusy');
const empty = await request(next, senior, seniorQueue, 'empty'); assert.equal(empty.status, 204);
const conflict = await request(next, senior, general, 'empty');
assert.equal(conflict.status, 409); assert.equal(conflict.value.code, 'IdempotencyConflict');
const third = await request(next, senior, general, 'ordinary-senior');
assert.equal(third.status, 200); schema('MyAssignment', third.value); assert.equal(third.value.check.state, 'Assigned');
assert.equal((await request(next, senior, seniorQueue, 'cross-queue')).value.code, 'ReviewerBusy');
const original = [race[0].value, second.value, third.value];
for (const [index, token] of [a, b, senior].entries()) {
  const current = await request('/me/assignment?tournamentId=ignored-filter', token);
  schema('MyAssignment', current.value); samePrivate(original[index], current.value);
  const id = current.value.check.checkId;
  const metadata = await request('/checks/' + id, b); schema('CheckView', metadata.value);
  assert.ok(!JSON.stringify(metadata.value).includes('capability'));
  const material = await request('/checks/' + id + '/material', token);
  assert.equal(material.status, 200);
  const expected = manifest.solutions.find(x => x.solutionId === current.value.check.solutionId);
  assert.equal(createHash('sha256').update(material.bytes).digest('hex'), expected.materialSha256);
}
assert.equal((await request('/checks/' + original[0].check.checkId + '/material', b)).status, 403);
assert.equal((await request('/checks/unknown-id', a)).status, 404);
if (process.argv.includes('--restart')) {
  const restart = spawnSync('docker', ['compose', '-f', 'infra/compose/compose.yaml', 'restart', 'checking-api'], { cwd: root, stdio: 'inherit' });
  assert.equal(restart.status, 0);
  const deadline = Date.now() + 30000;
  for (;;) {
    try { if ((await fetch(base + '/health/ready', { signal: AbortSignal.timeout(2000) })).status === 200) break; } catch {}
    assert.ok(Date.now() < deadline, 'Checking did not recover');
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  for (const [index, token] of [a, b, senior].entries()) samePrivate(original[index], (await request('/me/assignment', token)).value);
  samePrivate(original[0], (await request(next, a, general, 'lost-reply')).value);
  assert.equal((await request(next, senior, seniorQueue, 'empty')).status, 204);
}
console.log('Checking work HTTP: OpenAPI views, authorization, concurrent replay, shared occupancy, owner-only material and restart: OK');

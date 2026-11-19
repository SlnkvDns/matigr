import { readFile } from 'node:fs/promises';
import { createPublicKey, verify, createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import path from 'node:path';
import { root, validateHttp } from '../tests/contracts/support.mjs';

const base = 'http://127.0.0.1:5101';
const manifest = JSON.parse(await readFile(path.join(root, 'infra/fixtures/manifest.json'), 'utf8'));
const publicPath = path.join(root, '.local/secrets/dev-issuer-public.pem');
const publicBytes = await readFile(publicPath);
const publicKey = createPublicKey(publicBytes);
function verifyCredential(session, expected) {
  validateHttp('DevSession', session);
  const { testSubjectId, ...profile } = expected;
  assert.deepEqual(session.profile, profile);
  const parts = session.accessToken.split('.');
  assert.equal(parts.length, 3);
  const header = JSON.parse(Buffer.from(parts[0], 'base64url'));
  assert.equal(header.alg, 'RS256');
  assert.equal(header.typ, 'at+jwt');
  assert.ok(verify('RSA-SHA256', Buffer.from(`${parts[0]}.${parts[1]}`), publicKey, Buffer.from(parts[2], 'base64url')), 'Invalid signature');
  const claims = JSON.parse(Buffer.from(parts[1], 'base64url'));
  assert.equal(claims.iss, 'matigr-local');
  assert.equal(claims.aud, 'matigr-user-api');
  assert.equal(claims.token_use, 'user');
  assert.equal(claims.sub, expected.subjectId);
  assert.deepEqual([].concat(claims.role).sort(), [...expected.roles].sort());
  assert.deepEqual([].concat(claims.tournament_id).sort(), [...expected.tournamentIds].sort());
  assert.equal(claims.team_id, expected.teamId);
  assert.ok(claims.exp > Date.now() / 1000 && claims.nbf <= Date.now() / 1000);
  assert.equal(new Date(claims.exp * 1000).toISOString(), new Date(session.expiresAt).toISOString());
}
async function select(body, status = 200) {
  const response = await fetch(`${base}/dev/v1/session/select`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal: AbortSignal.timeout(5000),
  });
  assert.equal(response.status, status);
  assert.equal(response.headers.get('cache-control'), 'private, no-store');
  const result = await response.json();
  if (status !== 200) validateHttp('ProblemDetails', result);
  return result;
}
let previous;
for (const subject of manifest.subjects) {
  const session = await select({ testSubjectId: subject.testSubjectId });
  verifyCredential(session, subject);
  previous ??= session;
}
for (const property of ['role', 'roles', 'subjectId', 'teamId', 'tournamentIds'])
  await select({ testSubjectId: 'jury-a', [property]: 'browser-injection' }, 400);
await select({ testSubjectId: 'missing' }, 404);
if (process.argv.includes('--restart')) {
  const result = spawnSync('docker', ['compose', '-f', 'infra/compose/compose.yaml', 'restart', '--no-deps', 'checking-api'],
    { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const deadline = Date.now() + 60000;
  while (true) {
    try {
      if ((await fetch(`${base}/health/ready`, { signal: AbortSignal.timeout(2000) })).ok) break;
    } catch { /* startup in progress */ }
    assert.ok(Date.now() < deadline, 'Checking readiness deadline after restart');
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  assert.equal(createHash('sha256').update(await readFile(publicPath)).digest('hex'), createHash('sha256').update(publicBytes).digest('hex'));
  verifyCredential(previous, manifest.subjects[0]);
  verifyCredential(await select({ testSubjectId: manifest.subjects[0].testSubjectId }), manifest.subjects[0]);
}
console.log('Identity: 7 server profiles match OpenAPI, RS256/public-only verification, rejected browser authority, stable key/subject' +
  (process.argv.includes('--restart') ? ' across Compose restart: OK' : ': OK'));

import { readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const compose = ['compose', '-f', 'infra/compose/compose.yaml'];
const topology = JSON.parse(await readFile(path.join(root, 'infra/compose/topology.json'), 'utf8'));
function run(args, input) {
  const result = spawnSync('docker', [...compose, ...args], { cwd: root, input, encoding: 'utf8' });
  if (result.error) throw result.error;
  return result;
}
const cfgResult = run(['config', '--format', 'json']);
assert.equal(cfgResult.status, 0, cfgResult.stderr);
const cfg = JSON.parse(cfgResult.stdout);
assert.ok(cfg.services['checking-api'].volumes.some(v => v.target === '/fixtures' && v.read_only));
assert.ok(cfg.services['checking-api'].secrets.some(s => s.source === 'dev_issuer_private'));
for (const [name, service] of Object.entries(cfg.services))
  if (name !== 'checking-api') assert.ok(!(service.secrets ?? []).some(s => s.source === 'dev_issuer_private'));
const appealsApi = cfg.services['appeals-api'];
assert.deepEqual(Object.keys(appealsApi.networks), ['appeals-data', 'transport']);
assert.deepEqual(appealsApi.secrets.map(s => s.source), ['appeals_db_password']);
assert.ok(appealsApi.volumes.some(v => v.target === '/fixtures' && v.read_only));
for (const service of ['checking', 'results', 'appeals']) {
  const db = cfg.services[`${service}-db`];
  assert.deepEqual(Object.keys(db.networks), [`${service}-data`]);
  assert.equal(db.ports, undefined);
  assert.equal(db.volumes[0].source, `${service}-data`);
  for (const identity of ['checking', 'results', 'appeals']) {
    const password = await readFile(path.join(root, `.local/secrets/${identity}_db_password`), 'utf8');
    const result = run(['exec', '-T', `${service}-db`, 'sh', '-c',
      `read -r PGPASSWORD; export PGPASSWORD; exec psql -h 127.0.0.1 -U ${identity}_app -d ${service}_db -Atc 'SELECT current_database()'`], password + '\n');
    if (identity === service) {
      assert.equal(result.status, 0, `Own credentials failed for ${service}`);
      assert.equal(result.stdout.trim(), `${service}_db`);
    } else {
      assert.notEqual(result.status, 0, `Cross-service credentials succeeded: ${identity} -> ${service}`);
      assert.match(result.stderr, /authentication failed/);
    }
  }
}
const adminPassword = (await readFile(path.join(root, '.local/secrets/local-admin_broker_password'), 'utf8')).trim();
const headers = { Authorization: `Basic ${Buffer.from(`local-admin:${adminPassword}`).toString('base64')}` };
async function get(endpoint) {
  const response = await fetch(`http://127.0.0.1:15672/api/${endpoint}`, { headers, signal: AbortSignal.timeout(5000) });
  assert.equal(response.status, 200, `Broker management ${endpoint}`);
  return response.json();
}
const queues = await get('queues/matigr');
const bindings = await get('bindings/matigr');
const exchanges = await get('exchanges/matigr');
assert.ok(exchanges.some(e => e.name === topology.exchange && e.durable && e.type === 'topic'));
for (const b of topology.bindings) {
  assert.ok(queues.some(q => q.name === b.queue && q.durable));
  assert.ok(bindings.some(actual => actual.source === topology.exchange && actual.destination === b.queue && actual.routing_key === b.key));
}
const users = await get('users');
assert.ok(!users.some(u => u.name === 'guest'));
const topicPermissions = await get('vhosts/matigr/topic-permissions');
for (const service of ['checking', 'results', 'appeals']) {
  const permission = topicPermissions.find(p => p.user === service);
  assert.ok(permission);
  assert.ok(new RegExp(permission.write).test(`${service}.sample.v1`));
  assert.ok(!new RegExp(permission.write).test('other.sample.v1'));
}
const response = await fetch('http://127.0.0.1:5101/health/ready', { signal: AbortSignal.timeout(5000) });
assert.equal(response.status, 200);
assert.deepEqual(await response.json(), { status: 'Ready', database: true, broker: true });
const fixtureHash = run(['exec', '-T', 'checking-api', 'sha256sum', '/fixtures/manifest.json']);
assert.equal(fixtureHash.status, 0);
assert.equal(fixtureHash.stdout.split(/\s/)[0], (await readFile(path.join(root, 'infra/fixtures/manifest.sha256'), 'utf8')).trim());
const processIdentity = run(['exec', '-T', 'checking-api', 'sh', '-c', 'grep "^Uid:" /proc/1/status']);
assert.equal(processIdentity.status, 0);
assert.match(processIdentity.stdout, /^Uid:\s+1654\s+1654\s+1654\s+1654/m);
const appealsReady = await fetch('http://127.0.0.1:5103/health/ready', { signal: AbortSignal.timeout(5000) });
assert.equal(appealsReady.status, 200);
assert.deepEqual(await appealsReady.json(), { status: 'Ready', database: true });
const appealsPassword = (await readFile(path.join(root, '.local/secrets/appeals_db_password'), 'utf8')).trim();
function appealsSql(sql) {
  return run(['exec', '-T', 'appeals-db', 'sh', '-c',
    'read -r PGPASSWORD; export PGPASSWORD; exec psql -h 127.0.0.1 -U appeals_app -d appeals_db -At -v ON_ERROR_STOP=1'],
  `${appealsPassword}\n${sql}`);
}
const fixtureWindow = appealsSql("SELECT state FROM submission_windows WHERE tournament_id = '0344e06b-4ea1-582e-bd91-e832719773f2'");
assert.equal(fixtureWindow.status, 0, fixtureWindow.stderr);
assert.equal(fixtureWindow.stdout.trim(), 'Open', 'Appeals fixture window must start Open');
const migration = appealsSql("SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '202610230001_InitialSubmissionWindows'");
assert.equal(migration.status, 0, migration.stderr);
assert.equal(migration.stdout.trim(), '1', 'Appeals schema migration was not applied');
const closedPreservation = appealsSql(`
BEGIN;
INSERT INTO submission_windows (tournament_id, state, version)
VALUES ('f010a160-8042-41bc-972d-58b51b41ef11', 'Closed', 2)
ON CONFLICT (tournament_id) DO NOTHING;
INSERT INTO submission_windows (tournament_id, state, version)
VALUES ('f010a160-8042-41bc-972d-58b51b41ef11', 'Open', 1)
ON CONFLICT (tournament_id) DO NOTHING;
DO $$ BEGIN
  IF (SELECT state FROM submission_windows WHERE tournament_id = 'f010a160-8042-41bc-972d-58b51b41ef11') <> 'Closed' THEN
    RAISE EXCEPTION 'fixture bootstrap reopened a Closed submission window';
  END IF;
END $$;
ROLLBACK;
`);
assert.equal(closedPreservation.status, 0, closedPreservation.stderr);
const appealsIdentity = run(['exec', '-T', 'appeals-api', 'sh', '-c', 'grep "^Uid:" /proc/1/status']);
assert.equal(appealsIdentity.status, 0);
assert.match(appealsIdentity.stdout, /^Uid:\s+1654\s+1654\s+1654\s+1654/m);
console.log('Infrastructure: 3 own DB connections, 6 rejected cross-service connections, isolated networks/volumes, durable routing, producer permissions, Checking and Appeals readiness, Appeals migrations/Open fixture, non-root APIs: OK');

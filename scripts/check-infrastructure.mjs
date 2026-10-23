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
console.log('Infrastructure: 3 own DB connections, 6 rejected cross-service connections, isolated networks/volumes, durable routing, producer permissions, Checking readiness: OK');

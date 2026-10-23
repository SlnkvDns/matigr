import { mkdir, readFile, writeFile, chmod } from 'node:fs/promises';
import { randomBytes, createHash } from 'node:crypto';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const local = path.join(root, '.local');
const secrets = path.join(local, 'secrets');
await mkdir(secrets, { recursive: true, mode: 0o700 });
async function secret(name) {
  const file = path.join(secrets, name);
  try { await writeFile(file, randomBytes(32).toString('hex'), { flag: 'wx', mode: 0o600 }); }
  catch (e) { if (e.code !== 'EEXIST') throw e; }
  const value = (await readFile(file, 'utf8')).trim();
  if (!/^[a-f0-9]{64}$/.test(value)) throw new Error(`Invalid persisted secret: ${name}; refusing to rotate it`);
  return value;
}
for (const service of ['checking', 'results', 'appeals']) await secret(`${service}_db_password`);
const topology = JSON.parse(await readFile(path.join(root, 'infra/compose/topology.json'), 'utf8'));
const users = [];
for (const name of ['checking', 'results', 'appeals', 'local-admin']) {
  const password = await secret(`${name}_broker_password`);
  const salt = randomBytes(4);
  const hash = createHash('sha256').update(salt).update(password).digest();
  users.push({ name, password_hash: Buffer.concat([salt, hash]).toString('base64'),
    hashing_algorithm: 'rabbit_password_hashing_sha256', tags: name === 'local-admin' ? ['administrator'] : [] });
}
const definitions = {
  users, vhosts: [{ name: topology.vhost }],
  permissions: users.map(({ name }) => ({ user: name, vhost: topology.vhost,
    configure: name === 'local-admin' ? '.*' : '^$',
    write: name === 'local-admin' ? '.*' : '^matigr\\.v1$',
    read: name === 'local-admin' ? '.*' : `^${name}\\..*` })),
  topic_permissions: users.filter(u => u.name !== 'local-admin').map(({ name }) => ({
    user: name, vhost: topology.vhost, exchange: topology.exchange, write: `^${name}\\..*`, read: '^$' })),
  exchanges: [{ name: topology.exchange, vhost: topology.vhost, type: 'topic', durable: true, auto_delete: false, internal: false, arguments: {} }],
  queues: [...new Set(topology.bindings.map(b => b.queue))].map(name => ({ name, vhost: topology.vhost,
    durable: true, auto_delete: false, arguments: { 'x-queue-type': 'classic' } })),
  bindings: topology.bindings.map(b => ({ source: topology.exchange, vhost: topology.vhost, destination: b.queue,
    destination_type: 'queue', routing_key: b.key, arguments: {} }))
};
const definitionsFile = path.join(local, 'rabbitmq-definitions.json');
// Contains salted hashes of random 256-bit credentials, never plaintext passwords.
// The non-root RabbitMQ process must be able to read the bind mount on Linux.
await writeFile(definitionsFile, JSON.stringify(definitions, null, 2) + '\n', { mode: 0o644 });
await chmod(definitionsFile, 0o644);
console.log('Local database/broker secrets and topology prepared; existing credentials preserved.');

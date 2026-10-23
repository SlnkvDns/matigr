import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';

assert.ok(process.env.COMPOSE_PROJECT_NAME, 'Select a fresh test Compose project explicitly');
const project = process.env.COMPOSE_PROJECT_NAME;
export function compose(args) {
  const result = spawnSync('docker', ['compose', '-p', project, '-f', 'infra/compose/compose.yaml', ...args], { encoding: 'utf8' });
  assert.equal(result.status, 0, `Compose test operation failed: ${args[0]}`); return result.stdout;
}
export const quote = value => `'${String(value).replaceAll("'", "''")}'`;
export async function sql(service, query) {
  assert.ok(['checking', 'results'].includes(service));
  const password = (await readFile(new URL(`../.local/secrets/${service}_db_password`, import.meta.url), 'utf8')).trim();
  const r = spawnSync('docker', ['compose', '-p', project, '-f', 'infra/compose/compose.yaml', 'exec', '-T', `${service}-db`, 'sh', '-c',
    `read -r PGPASSWORD; export PGPASSWORD; exec psql -h 127.0.0.1 -U ${service}_app -d ${service}_db -At -v ON_ERROR_STOP=1`],
    { input: `${password}\n${query}\n`, encoding: 'utf8' });
  assert.equal(r.status, 0, `${service} test SQL failed`); return r.stdout.trim();
}
export async function eventually(read, predicate, description, timeout = 60000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const value = await read(); if (predicate(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  throw new Error(`Timed out: ${description}`);
}
export async function management(path, method = 'GET', body) {
  const password = (await readFile(new URL('../.local/secrets/local-admin_broker_password', import.meta.url), 'utf8')).trim();
  const r = await fetch('http://127.0.0.1:15672/api/' + path, { method,
    headers: { Authorization: `Basic ${Buffer.from(`local-admin:${password}`).toString('base64')}`, 'Content-Type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(5000) });
  assert.ok(r.ok, `Management ${method} failed (${r.status})`);
  const text = await r.text(); return text ? JSON.parse(text) : undefined;
}
export async function session(testSubjectId) {
  const r = await fetch('http://127.0.0.1:5101/dev/v1/session/select', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ testSubjectId }) });
  assert.equal(r.status, 200); return (await r.json()).accessToken;
}

import { spawnSync } from 'node:child_process';
import path from 'node:path';
const root = path.resolve(import.meta.dirname, '..');
const compose = ['compose', '-p', 'matigr-appeals-storage-tests', '-f', 'infra/compose/appeals-storage-tests.yaml'];
function run(args) {
  const result = spawnSync('docker', [...compose, ...args], { cwd: root, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Appeals storage test failed (${result.status})`);
}
try { run(['up', '-d', '--wait', '--wait-timeout', '90', 'database', 'broker']); run(['run', '--build', '--rm', 'tests']); }
finally { run(['down']); }

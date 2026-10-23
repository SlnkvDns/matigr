import { spawnSync } from 'node:child_process';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const compose = ['compose', '-p', 'matigr-checking-storage-tests', '-f', 'infra/compose/checking-storage-tests.yaml'];
function run(args) {
  const result = spawnSync('docker', [...compose, ...args], { cwd: root, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Checking storage test command failed (${result.status})`);
}
try {
  run(['up', '-d', '--wait', '--wait-timeout', '90', 'database']);
  run(['run', '--build', '--rm', 'tests']);
  // A separate OS process/container must restore the original protected assignment/HTTP receipt.
  run(['run', '--rm', '-e', 'StorageTest__RestartRead=true', 'tests', '--filter',
    'FullyQualifiedName~Capability_checkpoint_survives_process_restart']);
} finally {
  run(['down']);
}

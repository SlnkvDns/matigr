import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { loadFixtures, validateManifest } from '../../scripts/fixtures.mjs';
import { prepareKeys } from '../../scripts/prepare-fixtures.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const { manifest } = await loadFixtures(path.join(root, 'infra/fixtures'));
test('versioned fixture and all material checksums are valid', () => {
  assert.deepEqual(manifest.tasks.map(t => t.points), ['10', '20']);
  assert.equal(manifest.subjects.filter(s => s.roles.includes('Senior')).length, 1);
  assert.equal(manifest.solutions.length, 6);
});
test('a second solution for the same tournament/team/task is rejected', () => {
  const clone = structuredClone(manifest);
  clone.solutions[1].teamId = clone.solutions[0].teamId;
  clone.solutions[1].taskId = clone.solutions[0].taskId;
  assert.throws(() => validateManifest(clone), /Duplicate tuple/);
});
test('unknown team and unsafe material path are rejected', () => {
  const clone = structuredClone(manifest);
  clone.solutions[0].teamId = 'unknown';
  assert.throws(() => validateManifest(clone), /Unknown team/);
  clone.solutions[0] = { ...manifest.solutions[0], materialFile: '../secret.txt' };
  assert.throws(() => validateManifest(clone));
});
test('repeated preparation preserves issuer and capability keys; incomplete pair is rejected', async () => {
  const tempRoot = path.join(root, '.local/fixture-tests');
  await mkdir(tempRoot, { recursive: true });
  const directory = await mkdtemp(path.join(tempRoot, 'keys-'));
  await prepareKeys(directory);
  const names = ['dev-issuer-private.pem', 'dev-issuer-public.pem', 'capability-protection-key'];
  const before = await Promise.all(names.map(n => readFile(path.join(directory, n), 'utf8')));
  await prepareKeys(directory);
  assert.deepEqual(await Promise.all(names.map(n => readFile(path.join(directory, n), 'utf8'))), before);
  const broken = await mkdtemp(path.join(tempRoot, 'broken-'));
  await writeFile(path.join(broken, 'dev-issuer-public.pem'), before[1]);
  await assert.rejects(prepareKeys(broken), /Incomplete issuer/);
});
test('an edited manifest cannot silently become a new bootstrap version', async () => {
  const tempRoot = path.join(root, '.local/fixture-tests');
  await mkdir(tempRoot, { recursive: true });
  const directory = await mkdtemp(path.join(tempRoot, 'manifest-'));
  await writeFile(path.join(directory, 'manifest.json'), JSON.stringify({ ...manifest, fixtureVersion: '2' }));
  await writeFile(path.join(directory, 'manifest.sha256'), await readFile(path.join(root, 'infra/fixtures/manifest.sha256')));
  await assert.rejects(loadFixtures(directory), /Manifest checksum mismatch/);
});

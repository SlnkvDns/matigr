import { access, mkdir, readFile, writeFile } from 'node:fs/promises';
import { generateKeyPairSync, createPublicKey, randomBytes } from 'node:crypto';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
import { loadFixtures } from './fixtures.mjs';

export async function prepareKeys(directory) {
  await mkdir(directory, { recursive: true, mode: 0o700 });
  const privatePath = path.join(directory, 'dev-issuer-private.pem');
  const publicPath = path.join(directory, 'dev-issuer-public.pem');
  async function exists(file) { try { await access(file); return true; } catch (e) { if (e.code === 'ENOENT') return false; throw e; } }
  const hasPrivate = await exists(privatePath);
  const hasPublic = await exists(publicPath);
  assert.equal(hasPrivate, hasPublic, 'Incomplete issuer key pair; refusing silent key rotation');
  if (!hasPrivate) {
    const pair = generateKeyPairSync('rsa', { modulusLength: 3072,
      privateKeyEncoding: { type: 'pkcs8', format: 'pem' }, publicKeyEncoding: { type: 'spki', format: 'pem' } });
    await writeFile(privatePath, pair.privateKey, { flag: 'wx', mode: 0o600 });
    await writeFile(publicPath, pair.publicKey, { flag: 'wx', mode: 0o600 });
  }
  const expectedPublic = createPublicKey(await readFile(privatePath)).export({ type: 'spki', format: 'pem' });
  assert.equal(expectedPublic, await readFile(publicPath, 'utf8'), 'Issuer key pair mismatch');
  const capabilityPath = path.join(directory, 'capability-protection-key');
  if (!(await exists(capabilityPath))) await writeFile(capabilityPath, randomBytes(32).toString('hex'), { flag: 'wx', mode: 0o600 });
  assert.match(await readFile(capabilityPath, 'utf8'), /^[a-f0-9]{64}$/);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const root = path.resolve(import.meta.dirname, '..');
  const { manifest } = await loadFixtures(path.join(root, 'infra/fixtures'));
  await prepareKeys(path.join(root, '.local/secrets'));
  console.log(`Fixture v${manifest.fixtureVersion}: ${manifest.teams.length} teams, ${manifest.tasks.length} tasks, ${manifest.solutions.length} accepted references verified; issuer/capability keys persisted.`);
}

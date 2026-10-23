import { readdir, readFile, access } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';

const root = path.resolve(import.meta.dirname, '..');
async function files(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  return (await Promise.all(entries.map(e => e.isDirectory() ? files(path.join(directory, e.name)) : [path.join(directory, e.name)]))).flat();
}
const docs = [...(await files(path.join(root, 'docs'))).filter(f => f.endsWith('.md')),
  ...(await files(path.join(root, 'contracts'))).filter(f => f.endsWith('.md')),
  path.join(root, 'README.md'), path.join(root, 'AGENTS.md'), path.join(root, 'infra/fixtures/README.md')];
const tasks = new Map();
for (const file of docs) {
  const text = await readFile(file, 'utf8');
  for (const match of text.matchAll(/\[[^\]]+\]\(([^\s)]+)\)/g)) {
    if (/^(https?:|mailto:|#)/.test(match[1])) continue;
    const target = decodeURIComponent(match[1].split('#')[0]);
    await access(path.resolve(path.dirname(file), target)).catch(() => { throw new Error(`Broken link ${match[1]} in ${file}`); });
  }
  if (path.basename(file) !== 'tasks.md') continue;
  for (const line of text.split('\n')) {
    const columns = line.split('|').map(c => c.trim());
    if (!/^(CHK|RES|APL)-(INF|BE|INT|FE|UT|IT|E2E)-\d+$/.test(columns[1] ?? '')) continue;
    assert.ok(!tasks.has(columns[1]), `Duplicate task ${columns[1]}`);
    tasks.set(columns[1], columns[3].split(',').map(d => d.trim()));
  }
}
const done = new Set(['MVP-REVIEW']);
const active = new Set();
function visit(id) {
  if (done.has(id)) return;
  assert.ok(tasks.has(id), `Unknown task ${id}`);
  assert.ok(!active.has(id), `Cyclic dependency at ${id}`);
  active.add(id);
  for (const dependency of tasks.get(id)) visit(dependency);
  active.delete(id);
  done.add(id);
}
for (const id of tasks.keys()) visit(id);
console.log(`Documentation: local link targets and ${tasks.size} unique tasks with acyclic dependencies: OK`);

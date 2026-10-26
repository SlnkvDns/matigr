import { readdir, readFile, access } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';

const root = path.resolve(import.meta.dirname, '..');
async function files(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const excluded = new Set(['node_modules', 'bin', 'obj', '.git', '.local', 'dist', 'coverage']);
  return (await Promise.all(entries.filter(e => !excluded.has(e.name)).map(e => e.isDirectory() ? files(path.join(directory, e.name)) : [path.join(directory, e.name)]))).flat();
}
const docs = [...(await files(path.join(root, 'docs'))).filter(f => f.endsWith('.md')),
  ...(await files(path.join(root, 'contracts'))).filter(f => f.endsWith('.md')),
  ...(await files(path.join(root, 'src'))).filter(f => f.endsWith('.md')),
  ...(await files(path.join(root, 'tests/e2e'))).filter(f => f.endsWith('.md')),
  path.join(root, 'README.md'), path.join(root, 'AGENTS.md'), path.join(root, 'infra/fixtures/README.md')];
const anchors = new Map();
async function headingAnchors(file) {
  if (anchors.has(file)) return anchors.get(file);
  const names = new Set(); const counts = new Map(); let fence = false;
  for (const line of (await readFile(file, 'utf8')).split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) { fence = !fence; continue; }
    if (fence || !/^#{1,6}\s/.test(line)) continue;
    const slug = line.replace(/^#{1,6}\s+/, '').replace(/\s+#+\s*$/, '').trim().toLowerCase()
      .replace(/[^\p{L}\p{N}\p{M}_\-\s]/gu, '').replace(/\s/g, '-');
    const count = counts.get(slug) ?? 0; counts.set(slug, count + 1);
    names.add(count ? `${slug}-${count}` : slug);
  }
  anchors.set(file, names); return names;
}
const tasks = new Map();
for (const file of docs) {
  const text = await readFile(file, 'utf8');
  for (const match of text.matchAll(/\[[^\]]+\]\(([^\s)]+)\)/g)) {
    if (/^(https?:|mailto:)/.test(match[1])) continue;
    const [relative, fragment] = match[1].split('#');
    const target = relative ? path.resolve(path.dirname(file), decodeURIComponent(relative)) : file;
    await access(target).catch(() => { throw new Error(`Broken link ${match[1]} in ${file}`); });
    if (fragment && target.endsWith('.md')) {
      assert.ok((await headingAnchors(target)).has(decodeURIComponent(fragment)), `Broken heading ${match[1]} in ${file}`);
    }
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
console.log(`Documentation: ${docs.length} Markdown files, local links/headings and ${tasks.size} unique tasks with acyclic dependencies: OK`);

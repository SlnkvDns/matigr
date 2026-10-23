import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';

const root = path.resolve(import.meta.dirname, '..');
async function walk(dir) {
  const entries = await readdir(dir, { withFileTypes: true });
  const children = await Promise.all(entries.filter(e => !['bin', 'obj'].includes(e.name)).map(async e =>
    e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)]));
  return children.flat();
}
const allowed = { Domain: [], Application: ['Domain'], Infrastructure: ['Application'], Api: ['Application', 'Infrastructure'] };
for (const file of (await walk(path.join(root, 'src/backend/services'))).filter(f => f.endsWith('.csproj'))) {
  const [service, layer] = path.basename(file, '.csproj').split('.');
  assert.ok(allowed[layer], `Unknown layer: ${file}`);
  const xml = await readFile(file, 'utf8');
  for (const match of xml.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
    const targetPath = path.resolve(path.dirname(file), match[1]);
    await readFile(targetPath);
    const [targetService, targetLayer] = path.basename(targetPath, '.csproj').split('.');
    assert.equal(targetService, service, `Cross-service reference: ${file}`);
    assert.ok(allowed[layer].includes(targetLayer), `Forbidden ${layer} -> ${targetLayer}: ${file}`);
  }
  if (layer === 'Domain') assert.ok(!/<(?:PackageReference|FrameworkReference)\b/.test(xml), `Domain has framework dependency: ${file}`);
}
console.log('Architecture references: OK');

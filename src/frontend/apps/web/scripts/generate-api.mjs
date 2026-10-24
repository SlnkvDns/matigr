import { mkdir, readFile, writeFile } from 'node:fs/promises';
import openapiTS, { astToString } from 'openapi-typescript';

for (const service of ['checking', 'results', 'appeals']) {
const input = new URL(`../../../../../contracts/http/${service}/openapi.json`, import.meta.url);
const output = new URL(`../src/shared/api/generated/${service}.d.ts`, import.meta.url);
const ast = await openapiTS(input);
const source = `// Generated from contracts/http/${service}/openapi.json. Run npm run generate:api; do not edit.\n` + astToString(ast);
if (process.argv.includes('--check')) {
  if (await readFile(output, 'utf8') !== source) throw new Error(`${service} API types are stale. Run npm run generate:api.`);
  console.log(`${service} OpenAPI types: up to date`);
} else {
  await mkdir(new URL('.', output), { recursive: true });
  await writeFile(output, source, 'utf8');
  console.log(`${service} OpenAPI types generated`);
}
}

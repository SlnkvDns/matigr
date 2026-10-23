import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const spec = JSON.parse(await readFile(new URL('../contracts/http/results/openapi.json', import.meta.url)));
const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const ajv = new Ajv2020({ strict: false, allErrors: true }); addFormats(ajv);
ajv.addSchema({ $id: 'results', components: spec.components });
const validate = name => ajv.compile({ $ref: `results#/components/schemas/${name}` });
const standings = validate('StandingsSnapshot'), mine = validate('MyTournamentResults'), problem = validate('ProblemDetails');
const tournament = manifest.tournament.tournamentId;
const base = 'http://127.0.0.1:5102';
async function token(key) {
  const response = await fetch('http://127.0.0.1:5101/dev/v1/session/select', { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ testSubjectId: key }) });
  assert.equal(response.status, 200); return (await response.json()).accessToken;
}
const publicPath = `${base}/api/v1/results/tournaments/${tournament}/standings`;
const response = await fetch(publicPath);
assert.equal(response.status, 200);
assert.equal(response.headers.get('Cache-Control'), 'public, max-age=0, must-revalidate');
const table = await response.json(); assert.ok(standings(table), 'Invalid public DTO');
assert.equal(response.headers.get('ETag'), `"${table.snapshotVersion}"`);
assert.equal(table.tasks.length, manifest.tasks.length); assert.equal(table.teams.length, manifest.teams.length);
const teamToken = await token('team-alpha');
const privatePath = `${base}/api/v1/results/me/tournaments/${tournament}/results`;
const own = await fetch(privatePath, { headers: { Authorization: `Bearer ${teamToken}`, 'X-Team-Id': manifest.teams[1].teamId } });
assert.equal(own.status, 200); assert.equal(own.headers.get('Cache-Control'), 'private, no-store');
assert.ok(mine(await own.json()), 'Invalid private DTO');
const juryToken = await token('jury-a');
for (const [url, headers, status] of [
  [privatePath, {}, 401],
  [privatePath, { Authorization: `Bearer ${juryToken}` }, 403],
  [`${base}/internal/v1/results/unknown/appeal-basis?expectedResultVersion=1`, { Authorization: `Bearer ${teamToken}` }, 401]
]) {
  const result = await fetch(url, { headers }); assert.equal(result.status, status);
  assert.equal(result.headers.get('Cache-Control'), 'private, no-store');
  assert.ok(problem(await result.json()), 'Invalid ProblemDetails');
}
console.log('Results HTTP: real issuer, OpenAPI DTOs, anonymous table, own team and service/user separation: OK');

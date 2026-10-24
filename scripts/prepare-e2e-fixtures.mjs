// Only prepared, already accepted fixture references; no intake or upload API.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { session, sql, quote as q, eventually } from './transport-test-support.mjs';
const m = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
assert.equal(await sql('checking', "SELECT count(*) FROM checks WHERE state <> 'Queued'"), '0', 'Use a new isolated fixture project');
const jury = await session('jury-a'), senior = await session('senior');
const base = 'http://127.0.0.1:5101/api/v1/checking';
async function command(token, path, body, etag) {
  const r = await fetch(base + path, { method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID(), ...(etag ? { 'If-Match': etag } : {}) }, body: JSON.stringify(body) });
  assert.equal(r.status, 200, path + ' ' + r.status); return { body: await r.json(), etag: r.headers.get('ETag') };
}
// Deterministic seeded order, with Gamma task 2 kept Queued for completion after manual end.
for (let i = 0; i < 5; i++) {
  let a = await command(jury, '/me/assignments/next', { tournamentId: m.tournament.tournamentId, queue: 'general' });
  const solution = m.solutions.find(s => s.solutionId === a.body.check.solutionId); assert.ok(solution);
  assert.equal(solution.teamId, m.teams[Math.floor(i / 2)].teamId); assert.equal(solution.taskId, m.tasks[i % 2].taskId);
  let token = jury;
  if (i === 0) {
    await command(jury, `/checks/${a.body.check.checkId}/refuse`, { assignmentId: a.body.assignmentId, capability: a.body.capability }, a.etag);
    a = await command(senior, '/me/assignments/next', { tournamentId: m.tournament.tournamentId, queue: 'senior' }); token = senior;
  }
  await command(token, `/checks/${a.body.check.checkId}/complete`, { assignmentId: a.body.assignmentId, capability: a.body.capability, outcome: [0,3,4].includes(i) ? 'Correct' : 'Incorrect' }, a.etag);
}
await eventually(() => sql('results', 'SELECT count(*) FROM team_task_results'), x => x === '5', 'five automatic publications');
assert.equal(await sql('checking', "SELECT count(*) FROM checks WHERE state='Queued'"), '1');
console.log('Final shared fixture baseline: Alpha10 Beta20 Gamma10; Gamma task2 accepted/queued for post-end work; five verdicts through real commands/broker: OK');

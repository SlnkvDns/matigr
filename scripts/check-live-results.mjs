import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { session } from './transport-test-support.mjs';

const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const tournament = manifest.tournament.tournamentId;
const base = `http://127.0.0.1:5102/api/v1/results/tournaments/${tournament}/standings`;
async function subscribe() {
  const response = await fetch(base + '/events', { headers: { 'Last-Event-ID': 'ignored-old' }, signal: AbortSignal.timeout(30000) });
  assert.equal(response.status, 200); assert.equal(response.headers.get('Content-Type')?.split(';')[0], 'text/event-stream');
  assert.equal(response.headers.get('Cache-Control'), 'no-cache');
  const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = '';
  return { close: () => reader.cancel(), next: async () => {
    while (true) {
      const split = buffer.indexOf('\n\n');
      if (split >= 0) {
        const frame = buffer.slice(0, split); buffer = buffer.slice(split + 2);
        const line = frame.split('\n').find(x => x.startsWith('data: '));
        if (line) { const value = JSON.parse(line.slice(6)); assert.deepEqual(Object.keys(value).sort(), ['snapshotVersion', 'tournamentId']); assert.equal(value.tournamentId, tournament); return value; }
        continue;
      }
      const result = await reader.read(); assert.equal(result.done, false, 'SSE closed before version'); buffer += decoder.decode(result.value, { stream: true });
    }
  } };
}
const stream = await subscribe();
let final;
try {
  const initial = await stream.next(); const auth = await session('jury-b');
  const headers = { Authorization: `Bearer ${auth}`, 'Content-Type': 'application/json', 'Idempotency-Key': randomUUID() };
  const claim = await fetch('http://127.0.0.1:5101/api/v1/checking/me/assignments/next', { method: 'POST', headers, body: JSON.stringify({ tournamentId: tournament, queue: 'general' }) });
  let current = claim;
  if (claim.status === 409) current = await fetch('http://127.0.0.1:5101/api/v1/checking/me/assignment', { headers: { Authorization: `Bearer ${auth}` } });
  assert.equal(current.status, 200); const assignment = await current.json();
  headers['Idempotency-Key'] = randomUUID(); headers['If-Match'] = current.headers.get('ETag');
  const completed = await fetch(`http://127.0.0.1:5101/api/v1/checking/checks/${assignment.check.checkId}/complete`, { method: 'POST', headers,
    body: JSON.stringify({ assignmentId: assignment.assignmentId, capability: assignment.capability, outcome: 'Incorrect' }) });
  assert.equal(completed.status, 200);
  do { final = await stream.next(); } while (BigInt(final.snapshotVersion) <= BigInt(initial.snapshotVersion));
  const table = await (await fetch(base)).json(); assert.ok(BigInt(table.snapshotVersion) >= BigInt(final.snapshotVersion));
  const row = table.teams.find(x => x.teamId === assignment.check.teamId); const column = table.tasks.findIndex(x => x.taskId === assignment.check.taskId);
  assert.deepEqual(row.cells[column], { status: 'Published', score: '0' });
} finally { await stream.close(); }
const reconnected = await subscribe();
try { assert.ok(BigInt((await reconnected.next()).snapshotVersion) >= BigInt(final.snapshotVersion)); }
finally { await reconnected.close(); }
console.log('Live results: anonymous SSE handshake, real jury verdict through broker, exact snapshot reconciliation, Published zero and reconnect: OK');

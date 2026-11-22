import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { session } from './transport-test-support.mjs';

const manifest = JSON.parse(await readFile(new URL('../infra/fixtures/manifest.json', import.meta.url)));
const tournament = manifest.tournament.tournamentId;
const base = `http://127.0.0.1:5102/api/v1/results/tournaments/${tournament}/standings`;
function subscribe() {
  const socket = new WebSocket(base.replace('http:', 'ws:') + '/events');
  const frames = [];
  let notify;
  const receive = frame => { frames.push(frame); notify?.(); };
  socket.addEventListener('message', event => receive({ data: event.data }));
  socket.addEventListener('error', () => receive({ error: 'WebSocket connection failed' }));
  socket.addEventListener('close', () => receive({ error: 'WebSocket closed before version' }));
  return { close: () => socket.close(), next: async () => {
    if (frames.length === 0) {
      await new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error('No WebSocket frame after 30s')), 30_000);
        notify = () => { clearTimeout(timeout); notify = undefined; resolve(); };
      });
    }
    const frame = frames.shift();
    assert.equal(frame.error, undefined);
    const value = JSON.parse(frame.data);
    assert.deepEqual(Object.keys(value).sort(), ['snapshotVersion', 'tasks', 'teams', 'tournamentId']);
    assert.equal(value.tournamentId, tournament);
    return value;
  } };
}
const stream = subscribe();
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
  assert.deepEqual(final.teams.find(x => x.teamId === assignment.check.teamId).cells[column], row.cells[column]);
  assert.equal((await fetch(base, { headers: { 'If-None-Match': `"${table.snapshotVersion}"` } })).status, 304);
} finally { stream.close(); }
const reconnected = subscribe();
try { assert.ok(BigInt((await reconnected.next()).snapshotVersion) >= BigInt(final.snapshotVersion)); }
finally { reconnected.close(); }
console.log('Live results: anonymous WebSocket handshake, real jury verdict through broker, conditional GET, Published zero and reconnect: OK');

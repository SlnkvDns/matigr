import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { waitForHttpReady } from '../../scripts/http-test-support.mjs';

async function server(t, handler) {
  const instance = createServer(handler);
  await new Promise(resolve => instance.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    instance.closeAllConnections();
    await new Promise(resolve => instance.close(resolve));
  });
  return `http://127.0.0.1:${instance.address().port}/health/ready`;
}

test('readiness survives a closed socket and HTTP 503 before HTTP 200', async t => {
  let requests = 0;
  const url = await server(t, (req, res) => {
    requests++;
    if (requests === 1) { req.socket.destroy(); return; }
    res.writeHead(requests === 2 ? 503 : 200).end();
  });
  await waitForHttpReady(url, 'API recovery', 5000);
  assert.ok(requests >= 3);
});

test('HTTP 503 cannot be mistaken for recovery and has a deadline', async t => {
  const url = await server(t, (_req, res) => res.writeHead(503).end());
  await assert.rejects(waitForHttpReady(url, 'unavailable API', 300), /Timed out: unavailable API/);
});

test('a stalled request is bounded by the readiness deadline', async t => {
  const url = await server(t, () => {});
  await assert.rejects(waitForHttpReady(url, 'stalled API', 300), /Timed out: stalled API/);
});

test('an invalid URL fails directly instead of being retried as a startup error', async () => {
  await assert.rejects(waitForHttpReady('not-a-url', 'invalid URL', 300),
    error => error instanceof TypeError && error.cause?.code === 'ERR_INVALID_URL');
});

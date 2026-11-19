import { describe, expect, it, vi } from 'vitest';
import { ApiError, ApiNetworkError, ApiProtocolError, createHttpClient } from './http';

function jsonResponse(data: unknown, init: ResponseInit = {}) {
  return new Response(JSON.stringify(data), { ...init, headers: { 'Content-Type': 'application/json', ...init.headers } });
}
function problem(status = 409) {
  return { type: 'about:blank', title: 'Conflict', status, code: 'ReviewerBusy', correlationId: 'correlation-1' };
}

describe('HTTP JSON transport', () => {
  it('keeps an anonymous GET free of bearer/cookies and preserves exact versions and ETag', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse({ version: '9007199254740993' }, { headers: { ETag: '"9007199254740993"' } }));
    const result = await createHttpClient('/api/v1/checking', fetch).get<{ version: string }>('/checks/check-1');
    expect(result).toEqual({ status: 200, data: { version: '9007199254740993' }, etag: '"9007199254740993"' });
    const [url, init] = fetch.mock.calls[0]!;
    expect(url).toBe('/api/v1/checking/checks/check-1');
    expect(init).toMatchObject({ method: 'GET', credentials: 'omit', cache: 'no-store', redirect: 'error' });
    const headers = new Headers(init?.headers);
    expect(headers.has('Authorization')).toBe(false);
    expect(headers.has('Content-Type')).toBe(false);
    expect(init?.body).toBeUndefined();
  });

  it('sends only the supplied token/key/If-Match and keeps a deliberate retry identical', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => jsonResponse({ state: 'Completed' }));
    const client = createHttpClient('/api/v1/checking', fetch);
    const options = { accessToken: 'memory-token', idempotencyKey: 'same-command', ifMatch: '"9007199254740993"' };
    const body = { assignmentId: 'assignment-1', capability: 'owner-capability', outcome: 'Correct' };
    await client.command('/checks/check-1/complete', body, options);
    await client.command('/checks/check-1/complete', body, options);
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(fetch.mock.calls[0]).toEqual(fetch.mock.calls[1]);
    const headers = new Headers(fetch.mock.calls[0]![1]?.headers);
    expect(headers.get('Authorization')).toBe('Bearer memory-token');
    expect(headers.get('Idempotency-Key')).toBe('same-command');
    expect(headers.get('If-Match')).toBe('"9007199254740993"');
    expect(JSON.parse(fetch.mock.calls[0]![1]!.body as string)).toEqual(body);
  });

  it('does not retain a token between requests', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async () => jsonResponse({}));
    const client = createHttpClient('/api/v1/checking', fetch);
    await client.get('/me/assignment', { accessToken: 'one-request-only' });
    await client.get('/me/assignment');
    expect(new Headers(fetch.mock.calls[1]![1]?.headers).has('Authorization')).toBe(false);
  });

  it('returns 204 without trying to decode an empty JSON body', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(null, { status: 204 }));
    expect(await createHttpClient('/api/v1/checking', fetch).get('/me/assignment')).toEqual({ status: 204, data: undefined, etag: null });
  });

  it('reads ProblemDetails diagnostics without retaining raw fields or retrying a conflict', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(JSON.stringify({ ...problem(), detail: 'private-data', instance: '/private', token: 'secret' }),
      { status: 409, headers: { 'Content-Type': 'application/problem+json; charset=utf-8' } }));
    const request = createHttpClient('/api/v1/checking', fetch).command('/me/assignments/next', {}, { idempotencyKey: 'command-1' });
    const error: unknown = await request.catch(value => value);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).problem).toEqual(problem());
    expect((error as Error).message).not.toMatch(/secret|private-data/);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it.each([401, 403, 404, 409, 412, 428, 503])('preserves HTTP %s even for a non-JSON error body', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response('<html>private-error</html>', { status, headers: { 'Content-Type': 'text/html' } }));
    const error = await createHttpClient('/api/v1/checking', fetch).get('/checks/1').catch((value: unknown) => value);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(status);
    expect((error as ApiError).problem).toBeUndefined();
    expect(String(error)).not.toContain('private-error');
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it.each(['{broken', JSON.stringify({ ...problem(), status: 400 }), JSON.stringify({ code: 'MissingDiagnostics' })])('ignores malformed or contradictory ProblemDetails: %s', async body => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(body, { status: 409, headers: { 'Content-Type': 'application/problem+json' } }));
    await expect(createHttpClient('/api/v1/checking', fetch).get('/checks/1')).rejects.toMatchObject({ status: 409, problem: undefined });
  });

  it('does not retry a lost command response or generate a replacement idempotency key', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockRejectedValue(new TypeError('network: sensitive-url'));
    const client = createHttpClient('/api/v1/checking', fetch);
    await expect(client.command('/checks/1/complete', {}, { idempotencyKey: 'keep-this-key' })).rejects.toBeInstanceOf(ApiNetworkError);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(new Headers(fetch.mock.calls[0]![1]?.headers).get('Idempotency-Key')).toBe('keep-this-key');
  });

  it('passes cancellation through instead of turning it into a command failure', async () => {
    const controller = new AbortController();
    const abort = new DOMException('Aborted', 'AbortError');
    const fetch = vi.fn<typeof globalThis.fetch>().mockRejectedValue(abort);
    const request = createHttpClient('/api/v1/checking', fetch).get('/me/assignment', { signal: controller.signal });
    await expect(request).rejects.toBe(abort);
    expect(fetch.mock.calls[0]![1]?.signal).toBe(controller.signal);
  });

  it.each([
    ['text/html', '<html>private-data</html>'], ['application/json', '{broken'],
  ])('rejects an invalid successful response without exposing its body: %s', async (contentType, body) => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(new Response(body, { headers: { 'Content-Type': contentType } }));
    await expect(createHttpClient('/api/v1/checking', fetch).get('/me/assignment')).rejects.toBeInstanceOf(ApiProtocolError);
  });

  it.each([200, 503])('preserves cancellation while decoding HTTP %s body', async status => {
    const abort = new DOMException('Aborted while reading body', 'AbortError');
    const response = new Response('{}', { status, headers: { 'Content-Type': status === 200 ? 'application/json' : 'application/problem+json' } });
    vi.spyOn(response, 'json').mockRejectedValue(abort);
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(response);
    await expect(createHttpClient('/api/v1/checking', fetch).get('/me/assignment')).rejects.toBe(abort);
  });

  it.each(['https://external.invalid/api', '//external.invalid', '/../../../outside', '/checks/1#fragment'])('rejects an endpoint outside the selected service before sending credentials: %s', async path => {
    const fetch = vi.fn<typeof globalThis.fetch>();
    await expect(createHttpClient('/api/v1/checking', fetch).get(path, { accessToken: 'secret' })).rejects.toBeInstanceOf(TypeError);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('supports a root API endpoint and stateless POST without inventing a business idempotency receipt', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse({ accessToken: 'dev-token' }));
    await createHttpClient('/dev/v1', fetch).post('/session/select', { testSubjectId: 'jury-a' });
    expect(new Headers(fetch.mock.calls[0]![1]?.headers).has('Idempotency-Key')).toBe(false);
    const rootFetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse({}));
    await createHttpClient('/api/v1/appeals', rootFetch).command('', {}, { idempotencyKey: 'submit-1' });
    expect(rootFetch.mock.calls[0]![0]).toBe('/api/v1/appeals');
  });

  it('rejects a blank command key before making a request', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>();
    await expect(createHttpClient('/api/v1/checking', fetch).command('/me/assignments/next', {}, { idempotencyKey: '' })).rejects.toBeInstanceOf(TypeError);
    expect(fetch).not.toHaveBeenCalled();
  });
});

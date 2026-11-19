import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/http';
import type { IdentityProvider, SessionCredential } from './identity';
import { SessionController } from './SessionController';
import { credential, deferred, jsonResponse } from './test-support';

const controllers: SessionController[] = [];
function controller(select: IdentityProvider['select'] = async key => credential(key)) {
  const result = new SessionController({ choices: [], select });
  controllers.push(result);
  return result;
}
afterEach(() => { for (const item of controllers.splice(0)) item.signOut(); vi.useRealTimers(); });

describe('in-memory session lifecycle', () => {
  it('starts anonymously every time and does not read/write browser persistence or expose the bearer in snapshots', async () => {
    const get = vi.spyOn(Storage.prototype, 'getItem');
    const set = vi.spyOn(Storage.prototype, 'setItem');
    const cookieGet = vi.spyOn(Document.prototype, 'cookie', 'get');
    const cookieSet = vi.spyOn(Document.prototype, 'cookie', 'set');
    const session = controller();
    expect(session.getSnapshot().status).toBe('anonymous');
    await session.select('jury-a');
    expect(session.getSnapshot().profile?.subjectId).toBe('subject-jury-a');
    expect(JSON.stringify(session.getSnapshot())).not.toContain('memory-secret');
    expect(controller().getSnapshot().status).toBe('anonymous');
    for (const spy of [get, set, cookieGet, cookieSet]) expect(spy).not.toHaveBeenCalled();
  });

  it.each(['success', 'failure'])('a late old selection cannot replace the latest profile or error (%s)', async outcome => {
    const first = deferred<SessionCredential>();
    const second = deferred<SessionCredential>();
    const select = vi.fn<IdentityProvider['select']>().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const session = controller(select);
    const one = session.select('jury-a');
    const two = session.select('jury-b');
    expect(select.mock.calls[0]![1].aborted).toBe(true);
    second.resolve(credential('jury-b'));
    await two;
    if (outcome === 'success') first.resolve(credential('jury-a')); else first.reject(new ApiError(503));
    await one;
    expect(session.getSnapshot()).toMatchObject({ status: 'authenticated', profile: { displayName: 'jury-b' } });
    expect(session.getSnapshot().error).toBeUndefined();
  });

  it('logout cancels pending selection and prevents its late response from signing in', async () => {
    const pending = deferred<SessionCredential>();
    const session = controller(() => pending.promise);
    const request = session.select('jury-a');
    session.signOut();
    pending.resolve(credential());
    await request;
    expect(session.getSnapshot().status).toBe('anonymous');
    expect(() => session.workspace()).toThrow();
  });

  it('drops the old profile, requests, cache, material URLs and callable actions before switching resolves, including same-subject selection', async () => {
    const pending = deferred<SessionCredential>();
    const select = vi.fn<IdentityProvider['select']>().mockResolvedValueOnce(credential()).mockReturnValueOnce(pending.promise);
    const session = controller(select);
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = vi.fn(() => 'blob:private-material');
      static revokeObjectURL = vi.fn();
    });
    await session.select('jury-a');
    const oldRevision = session.getSnapshot().revision;
    const workspace = session.workspace();
    workspace.cache('assignment', { capability: 'private-capability' });
    workspace.materialUrl(new Blob(['private-material']));
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse({}));
    vi.stubGlobal('fetch', fetch);
    const client = workspace.client('/api/v1/checking');
    const selection = session.select('jury-a');
    expect(session.getSnapshot()).toMatchObject({ status: 'selecting', revision: oldRevision + 1 });
    expect(session.getSnapshot().profile).toBeUndefined();
    expect(workspace.signal.aborted).toBe(true);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:private-material');
    expect(() => workspace.getCached('assignment')).toThrow();
    await expect(client.command('/checks/1/complete', {}, { idempotencyKey: 'old-action' })).rejects.toMatchObject({ name: 'AbortError' });
    expect(fetch).not.toHaveBeenCalled();
    pending.resolve(credential());
    await selection;
    expect(session.workspace().getCached('assignment')).toBeUndefined();
    expect(session.workspace()).not.toBe(workspace);
  });

  it.each([200, 401])('a late private HTTP %s response cannot affect the next session, even if fetch ignored abort', async status => {
    const session = controller();
    await session.select('jury-a');
    const pending = deferred<Response>();
    const fetch = vi.fn<typeof globalThis.fetch>().mockReturnValue(pending.promise);
    vi.stubGlobal('fetch', fetch);
    const old = session.workspace();
    const request = old.client('/api/v1/checking').get('/me/assignment');
    const assertion = expect(request).rejects.toMatchObject({ name: 'AbortError' });
    await session.select('jury-b');
    pending.resolve(jsonResponse({ privateData: 'jury-a' }, status));
    await assertion;
    expect(session.getSnapshot().profile?.displayName).toBe('jury-b');
    expect(() => old.cache('late', 'private')).toThrow();
  });

  it('sends the current bearer only to private calls, keeps command preconditions, and clears a current 401 without replay', async () => {
    const session = controller();
    await session.select('jury-a');
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(jsonResponse({ version: '9007199254740993' }))
      .mockResolvedValueOnce(jsonResponse({}, 401));
    vi.stubGlobal('fetch', fetch);
    const client = session.workspace().client('/api/v1/checking');
    await client.command('/checks/1/complete', { outcome: 'Correct' }, { idempotencyKey: 'same-key', ifMatch: '"9007199254740993"' });
    const headers = new Headers(fetch.mock.calls[0]![1]?.headers);
    expect(headers.get('Authorization')).toBe('Bearer memory-secret-jury-a');
    expect(headers.get('Idempotency-Key')).toBe('same-key');
    expect(headers.get('If-Match')).toBe('"9007199254740993"');
    await expect(client.get('/me/assignment')).rejects.toMatchObject({ status: 401 });
    expect(session.getSnapshot()).toMatchObject({ status: 'anonymous', notice: 'unauthorized' });
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('a 403 is an operation denial and does not erase the authenticated session', async () => {
    const session = controller();
    await session.select('jury-a');
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({}, 403)));
    await expect(session.workspace().client('/api/v1/checking').get('/queues?queue=senior')).rejects.toMatchObject({ status: 403 });
    expect(session.getSnapshot().status).toBe('authenticated');
  });

  it('caller cancellation remains AbortError even if fetch later returns 401, without invalidating the current session', async () => {
    const session = controller();
    await session.select('jury-a');
    const pending = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockReturnValue(pending.promise));
    const cancellation = new AbortController();
    const request = session.workspace().client('/api/v1/checking').get('/me/assignment', { signal: cancellation.signal });
    const assertion = expect(request).rejects.toMatchObject({ name: 'AbortError' });
    cancellation.abort();
    pending.resolve(jsonResponse({}, 401));
    await assertion;
    expect(session.getSnapshot().status).toBe('authenticated');
  });

  it('expiry clears private state without issuing any business command or automatically selecting again', async () => {
    vi.useFakeTimers();
    const select = vi.fn<IdentityProvider['select']>().mockResolvedValue(credential('jury-a', Date.now() + 1000));
    const session = controller(select);
    await session.select('jury-a');
    const old = session.workspace();
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    await vi.advanceTimersByTimeAsync(1000);
    expect(session.getSnapshot()).toMatchObject({ status: 'anonymous', notice: 'expired' });
    expect(old.signal.aborted).toBe(true);
    expect(select).toHaveBeenCalledTimes(1);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('failed switching leaves no previous identity available and permits an explicit retry', async () => {
    const select = vi.fn<IdentityProvider['select']>().mockResolvedValueOnce(credential())
      .mockRejectedValueOnce(new ApiError(503)).mockResolvedValueOnce(credential('jury-b'));
    const session = controller(select);
    await session.select('jury-a');
    const old = session.workspace();
    await session.select('jury-b');
    expect(session.getSnapshot()).toMatchObject({ status: 'anonymous', error: { status: 503 } });
    expect(session.getSnapshot().profile).toBeUndefined();
    expect(old.signal.aborted).toBe(true);
    await session.select('jury-b');
    expect(session.getSnapshot().profile?.displayName).toBe('jury-b');
    expect(select).toHaveBeenCalledTimes(3);
  });
});

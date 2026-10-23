import { describe, expect, it, vi } from 'vitest';
import { ApiError, ApiProtocolError } from '../api/http';
import { createDevIdentityProvider, readCredential } from './identity';
import { jsonResponse, wireSession } from './test-support';

describe('server identity adapter', () => {
  it('sends only the fixture key, with no previous credentials, roles or business receipt', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse(wireSession()));
    vi.stubGlobal('fetch', fetch);
    const signal = new AbortController().signal;
    const result = await createDevIdentityProvider().select('jury-a', signal);
    const [path, init] = fetch.mock.calls[0]!;
    expect(path).toBe('/dev/v1/session/select');
    expect(JSON.parse(init!.body as string)).toEqual({ testSubjectId: 'jury-a' });
    const headers = new Headers(init?.headers);
    for (const header of ['Authorization', 'Idempotency-Key', 'If-Match', 'X-Role', 'X-Team-Id']) expect(headers.has(header)).toBe(false);
    expect(init).toMatchObject({ credentials: 'omit', cache: 'no-store', redirect: 'error', signal });
    expect(result.profile).toEqual(wireSession().profile);
    expect(Object.isFrozen(result.profile.roles)).toBe(true);
  });

  it('uses only the returned profile and copies allowed fields, without decoding the token or deriving a role from the choice', async () => {
    const wire = { ...wireSession(), accessToken: 'opaque-not-a-browser-readable-jwt', profile: {
      subjectId: 'server-subject', displayName: 'Команда с сервера', roles: ['Team'], tournamentIds: ['server-tournament'],
      teamId: 'server-team', browserExtras: 'discard',
    } };
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(jsonResponse(wire)));
    const result = await createDevIdentityProvider().select('senior', new AbortController().signal);
    expect(result.profile).toEqual({ subjectId: 'server-subject', displayName: 'Команда с сервера', roles: ['Team'],
      tournamentIds: ['server-tournament'], teamId: 'server-team' });
    expect(result.accessToken).toBe(wire.accessToken);
  });

  it.each([
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, accessToken: '' }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, tokenType: 'Cookie' }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, expiresAt: 'invalid' }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, expiresAt: new Date(Date.now() - 1000).toISOString() }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, profile: null }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, profile: { ...wire.profile, subjectId: '' } }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, profile: { ...wire.profile, roles: [] } }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, profile: { ...wire.profile, roles: ['Admin'] } }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, profile: { ...wire.profile, roles: ['Jury', 'Jury'] } }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, profile: { ...wire.profile, roles: ['Team'] } }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, profile: { ...wire.profile, teamId: 'injected' } }),
    (wire: ReturnType<typeof wireSession>) => ({ ...wire, profile: { ...wire.profile, tournamentIds: ['same', 'same'] } }),
  ])('rejects malformed successful credentials before exposing any profile (%#)', mutate => {
    expect(() => readCredential(mutate(wireSession()))).toThrow(ApiProtocolError);
  });

  it.each([204, 404, 503])('does not turn HTTP %s into a session or automatically retry issuance', async status => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(status === 204 ? new Response(null, { status }) : jsonResponse({}, status));
    vi.stubGlobal('fetch', fetch);
    await expect(createDevIdentityProvider().select('jury-a', new AbortController().signal))
      .rejects.toBeInstanceOf(status === 204 ? ApiProtocolError : ApiError);
    expect(fetch).toHaveBeenCalledTimes(1);
  });
});

import { useEffect } from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { expect, it, vi } from 'vitest';
import { JuryAppeals } from './JuryAppeals';
import { SessionProvider, PrivateSessionBoundary, useSession } from '../../shared/auth/SessionContext';
import { credential, deferred, jsonResponse } from '../../shared/auth/test-support';

const item = () => ({ appealId: 'a', tournamentId: 't', teamId: 'team', taskId: 'task', state: 'Registered', version: '1', submittedAt: '2026-10-24T00:00:00Z' });
const standings = { tournamentId: 't', snapshotVersion: '1', tasks: [{ taskId: 'task', name: 'Задача 2' }],
  teams: [{ teamId: 'team', name: 'Альфа', total: '0', rank: 1, cells: [{ status: 'Pending' as const }] }] };
const detail = (state = 'AcceptedAwaitingRecheck', version = '2') => ({ ...item(), state, version, resultId: 'r', resultVersion: '1', baseCheckId: 'base', basePublicationId: 'p', baseOutcome: 'Correct', recheckRequestId: 'request' });
it('senior queue and assignment never restore Registered choice or the released selected assignment', async () => {
  let assigned = false;
  setup(vi.fn(async (url, init) => {
    if (init?.method === 'POST') return jsonResponse({ appealId: 'a', state: 'AcceptedAwaitingRecheck', version: '2' }, 202);
    if (String(url).includes('/tournaments/')) return jsonResponse({ tournamentId: 't', items: [item()] });
    if (String(url).includes('/checking/')) return new Response(null, { status: 204 });
    return jsonResponse({ ...detail('AwaitingSeniorReview', assigned ? '5' : '4'), checkId: 'check', reviewState: assigned ? 'SeniorAssigned' : 'SeniorQueued' });
  }));
  fireEvent.click(await screen.findByRole('button', { name: 'Взять на перепроверку' }));
  await screen.findByText('Перепроверка в очереди старшего жюри');
  expect(screen.queryByText('Взять снова')).toBeNull(); expect(screen.queryByText('Открыть перепроверку')).toBeNull();
  assigned = true; fireEvent.click(screen.getByText('Обновить статус'));
  await screen.findByText('Перепроверка назначена старшему жюри');
  expect(screen.queryByText('Открыть перепроверку')).toBeNull();
});
function setup(fetcher: typeof fetch) {
  vi.stubGlobal('fetch', fetcher);
  function Main() {
    const { session, snapshot } = useSession(); useEffect(() => { void session.select('jury'); }, [session]);
    return <><button onClick={() => session.signOut()}>Logout</button>{snapshot.profile && <PrivateSessionBoundary><JuryAppeals tournament="t" standings={standings} /></PrivateSessionBoundary>}</>;
  }
  return render(<SessionProvider provider={{ choices: [], select: async () => ({ ...credential('jury'), profile: { ...credential('jury').profile, tournamentIds: ['t'] } }) }}>
    <MemoryRouter><Main /></MemoryRouter></SessionProvider>);
}
it('queue read never claims; 202 stays pending until server detail and exact own Checking assignment confirm it', async () => {
  let phase = 'queue'; let owner = false; const posts: RequestInit[] = [];
  const fetcher = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'POST') { posts.push(init); phase = 'pending'; return jsonResponse({ appealId: 'a', state: 'AcceptedAwaitingRecheck', version: '2' }, 202); }
    if (String(url).includes('/tournaments/')) return jsonResponse({ tournamentId: 't', items: phase === 'queue' ? [item()] : [] });
    if (String(url).includes('/checking/')) return jsonResponse({ check: { checkId: owner ? 'check' : 'other' }, reviewerId: 'subject-jury' });
    return jsonResponse(phase === 'pending' ? detail() : { ...detail('RecheckPending', '3'), checkId: 'check' });
  });
  setup(fetcher); const choose = await screen.findByRole('button', { name: 'Взять на перепроверку' }); expect(posts).toHaveLength(0);
  expect(screen.getByText('Команда «Альфа» · Задача 2')).toBeVisible();
  fireEvent.click(choose); await screen.findByText(/когда назначение подтвердится/);
  expect(screen.getByRole('heading', { name: 'Выбранная апелляция' })).toHaveFocus();
  expect(screen.queryByRole('link', { name: 'Открыть перепроверку' })).toBeNull();
  phase = 'assigned'; fireEvent.click(screen.getByText('Обновить статус')); await screen.findByText('На перепроверке');
  expect(screen.queryByRole('link', { name: 'Открыть перепроверку' })).toBeNull();
  owner = true; fireEvent.click(screen.getByText('Обновить статус'));
  expect(await screen.findByRole('link', { name: 'Открыть перепроверку' })).toHaveAttribute('href', '/checking'); expect(posts).toHaveLength(1);
});
it('confirmed busy permits a new explicit manual choice of the same Appeal with a fresh key and current ETag', async () => {
  const posts: RequestInit[] = [];
  setup(vi.fn(async (url, init) => {
    if (init?.method === 'POST') { posts.push(init); return jsonResponse({ appealId: 'a', state: 'AcceptedAwaitingRecheck', version: posts.length === 1 ? '2' : '4' }, 202); }
    if (String(url).includes('/tournaments/')) return jsonResponse({ tournamentId: 't', items: posts.length === 0 ? [item()] : [] });
    return jsonResponse(posts.length === 1 ? detail('Registered', '3') : detail('AcceptedAwaitingRecheck', '4'));
  }));
  fireEvent.click(await screen.findByRole('button', { name: 'Взять на перепроверку' }));
  const again = await screen.findByRole('button', { name: 'Взять снова' }); expect(posts).toHaveLength(1);
  fireEvent.click(again); await screen.findByText(/когда назначение подтвердится/); expect(posts).toHaveLength(2);
  expect(new Headers(posts[0]!.headers).get('Idempotency-Key')).not.toBe(new Headers(posts[1]!.headers).get('Idempotency-Key'));
  expect(new Headers(posts[1]!.headers).get('If-Match')).toBe('"3"'); expect(posts[1]!.body).toBeUndefined();
});
it('uncertain manual claim retries the original key and If-Match without selecting another appeal', async () => {
  const uncertain = deferred<Response>(); const posts: RequestInit[] = [];
  setup(vi.fn(async (url, init) => {
    if (init?.method === 'POST') { posts.push(init); return posts.length === 1 ? uncertain.promise : jsonResponse({ appealId: 'a', state: 'AcceptedAwaitingRecheck', version: '2' }, 202); }
    return jsonResponse(String(url).includes('/tournaments/') ? { tournamentId: 't', items: [item()] } : detail());
  }));
  fireEvent.click(await screen.findByRole('button', { name: 'Взять на перепроверку' })); await act(async () => uncertain.reject(new TypeError('lost reply')));
  fireEvent.click(await screen.findByText('Повторить запрос')); await screen.findByText(/когда назначение подтвердится/);
  expect(posts).toHaveLength(2); expect(new Headers(posts[0]!.headers).get('Idempotency-Key')).toBe(new Headers(posts[1]!.headers).get('Idempotency-Key'));
  expect(new Headers(posts[1]!.headers).get('If-Match')).toBe('"1"');
});

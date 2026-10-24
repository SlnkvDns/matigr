import { useEffect } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { expect, it, vi } from 'vitest';
import { AppealsPage } from './AppealsPage';
import { readList, readView } from './appeals';
import { SessionProvider, PrivateSessionBoundary, useSession } from '../../shared/auth/SessionContext';
import { credential, deferred, jsonResponse } from '../../shared/auth/test-support';

const view = () => ({ appealId: 'a', tournamentId: 't', teamId: 'team', taskId: 'task', state: 'Registered', version: '1',
  resultId: 'r', resultVersion: '1', baseCheckId: 'check', basePublicationId: 'p', baseOutcome: 'Correct', submittedAt: '2026-10-24T00:00:00Z' });
it('protected refresh preserves a newer Closed publication when the server later returns an older process snapshot', async () => {
  let old = false;
  setup(vi.fn(async url => {
    const d = old ? view() : { ...view(), state: 'Closed', version: '9007199254740993', finalPublicationId: 'final-publication' };
    return jsonResponse(String(url).includes('/me?') ? { tournamentId: 't', items: [{ ...summary(), state: d.state, version: d.version }] } : d);
  }));
  await screen.findByText('Итоговая публикация: final-publication'); old = true;
  fireEvent.click(screen.getByText('Обновить мои апелляции'));
  await waitFor(() => expect(screen.getByText('Обновить мои апелляции')).toBeEnabled());
  expect(screen.getByText('Итоговая публикация: final-publication')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Подать апелляцию' })).toBeNull();
});
function summary(v = view()) { const { resultId: _r, resultVersion: _v, baseCheckId: _c, basePublicationId: _p, baseOutcome: _o, ...s } = v; return s; }
function setup(fetcher: typeof fetch, version = '1') {
  vi.stubGlobal('fetch', fetcher);
  function Main() {
    const { session } = useSession(); useEffect(() => { void session.select('team'); }, [session]);
    return <><button onClick={() => session.signOut()}>Logout</button><PrivateSessionBoundary><AppealsPage /></PrivateSessionBoundary></>;
  }
  return render(<SessionProvider provider={{ choices: [], select: async () => ({ ...credential('team'), profile: {
    ...credential('team').profile, roles: ['Team'], tournamentIds: ['t'], teamId: 'team' } }) }}>
    <MemoryRouter initialEntries={[`/appeals?tournamentId=t&resultId=r&expectedResultVersion=${version}`]}><Main /></MemoryRouter></SessionProvider>);
}
it('double click sends one operation; uncertain response retries original body/key and Registered never auto-claims', async () => {
  const uncertain = deferred<Response>(); let submitted = false; const posts: RequestInit[] = [];
  const fetcher = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'POST') { posts.push(init); if (posts.length === 1) return uncertain.promise; submitted = true; return jsonResponse(view(), 201); }
    if (String(url).includes('/me?')) return jsonResponse({ tournamentId: 't', items: submitted ? [summary()] : [] });
    return jsonResponse(view());
  });
  setup(fetcher);
  await waitFor(() => expect(screen.getByRole('button', { name: 'Подать апелляцию' })).toBeEnabled());
  const button = screen.getByRole('button', { name: 'Подать апелляцию' }); fireEvent.click(button); fireEvent.click(button);
  expect(posts).toHaveLength(1);
  await act(async () => uncertain.reject(new TypeError('lost response')));
  fireEvent.click(await screen.findByRole('button', { name: 'Повторить подачу' }));
  await screen.findByText(/Апелляция зарегистрирована/);
  expect(posts).toHaveLength(2); expect(posts[0]!.body).toBe(posts[1]!.body);
  expect(new Headers(posts[0]!.headers).get('Idempotency-Key')).toBe(new Headers(posts[1]!.headers).get('Idempotency-Key'));
  expect(fetcher.mock.calls.every(([url]) => !String(url).includes('/checking') && !String(url).includes('/claim'))).toBe(true);
});
it('an existing appeal hides the form even when a new result version is selected', async () => {
  const fetcher = vi.fn(async (url: RequestInfo | URL) => jsonResponse(String(url).includes('/me?') ? { tournamentId: 't', items: [summary()] } : view()));
  setup(fetcher, '9007199254740993');
  await screen.findByText(/Новая версия результата не разрешает вторую подачу/);
  expect(screen.queryByRole('button', { name: 'Подать апелляцию' })).toBeNull();
});
it('switching subject clears pending action and late successful response cannot restore private content', async () => {
  const pending = deferred<Response>();
  setup(vi.fn(async (_url, init) => init?.method === 'POST' ? pending.promise : jsonResponse({ tournamentId: 't', items: [] })));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Подать апелляцию' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Подать апелляцию' })); fireEvent.click(screen.getByText('Logout'));
  expect(screen.queryByRole('region', { name: 'Мои апелляции' })).toBeNull();
  await act(async () => pending.resolve(jsonResponse(view(), 201)));
  expect(screen.queryByText(/Апелляция зарегистрирована/)).toBeNull();
});
it.each(['WindowClosed', 'BaseNotAppealable', 'ResultVersionConflict', 'AlreadyAppealed', 'ResultsUnavailable'])('shows server admission error %s without automatic rebasing or a new key', async code => {
  let duplicate = false;
  const fetcher = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'POST') { duplicate = code === 'AlreadyAppealed'; return new Response(JSON.stringify({ type: 'about:blank', title: 'Error', status: code === 'ResultsUnavailable' ? 503 : 409,
      code, correlationId: 'safe-reference' }), { status: code === 'ResultsUnavailable' ? 503 : 409, headers: { 'Content-Type': 'application/problem+json' } }); }
    return jsonResponse(String(url).includes('/me?') ? { tournamentId: 't', items: duplicate ? [summary()] : [] } : view());
  });
  setup(fetcher); await waitFor(() => expect(screen.getByRole('button', { name: 'Подать апелляцию' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Подать апелляцию' })); await screen.findByRole('alert');
  expect(fetcher.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1);
  if (code === 'AlreadyAppealed') await screen.findByText(/Новая версия результата/);
  if (code === 'WindowClosed' || code === 'BaseNotAppealable') expect(screen.getByRole('button', { name: 'Повторить подачу' })).toBeDisabled();
});
it('rejects foreign-team list and extra private fields in views', () => {
  expect(() => readList({ tournamentId: 't', items: [{ ...summary(), teamId: 'foreign' }] }, 't', 'team')).toThrow();
  expect(() => readView({ ...view(), capability: 'secret' }, 't')).toThrow();
});

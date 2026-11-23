import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { expect, it, vi } from 'vitest';
import { TeamResults, readTeamResults } from './TeamResults';
import { SessionProvider, useSession } from '../../shared/auth/SessionContext';
import { credential, deferred, jsonResponse } from '../../shared/auth/test-support';
import type { IdentityProvider, SessionCredential } from '../../shared/auth/identity';

const mine = (version = '9007199254740993') => ({ tournamentId: 't', results: [{ taskId: 'task', resultId: 'r', resultVersion: version,
  publicationId: 'p', outcome: 'Incorrect', score: '0', appealAvailabilityHint: 'NotAuthoritative' }] });
const provider: IdentityProvider = { choices: [], select: async key => ({ ...credential(key), profile: {
  ...credential(key).profile, roles: ['Team'], teamId: key, tournamentIds: ['t', 'other'] } }) };
function Switch() {
  const { session } = useSession();
  return <><button onClick={() => void session.select('a')}>Team A</button><button onClick={() => void session.select('b')}>Team B</button>
    <button onClick={() => session.signOut()}>Logout</button></>;
}
function setup(identity = provider, snapshotVersion?: string) {
  const page = (tournament: string, version?: string) => <SessionProvider provider={identity}>
    <MemoryRouter><Switch /><TeamResults tournament={tournament} snapshotVersion={version} /></MemoryRouter>
  </SessionProvider>;
  const { rerender } = render(page('t', snapshotVersion));
  return (tournament: string, version?: string) => rerender(page(tournament, version));
}
it('loads only authenticated own results and passes exact server identity/version to Appeals without granting permission', async () => {
  const fetcher = vi.fn(async () => jsonResponse(mine())); vi.stubGlobal('fetch', fetcher); setup();
  expect(fetcher).not.toHaveBeenCalled(); fireEvent.click(screen.getByText('Team A'));
  const link = await screen.findByRole('link', { name: 'Перейти к апелляции' });
  expect(link).toHaveAttribute('href', '/appeals?tournamentId=t&resultId=r&expectedResultVersion=9007199254740993');
  expect(screen.getByText(/Неправильно, баллы: 0/)).toBeInTheDocument();
  expect(screen.getByText(/Допустимость подачи проверяет/)).toBeInTheDocument();
  const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit];
  expect(url).toBe('/api/v1/results/me/tournaments/t/results');
  expect(new Headers(init.headers).get('Authorization')).toBe('Bearer memory-secret-a');
  expect(init.cache).toBe('no-store');
});
it('coalesces public updates during slow personal reads and keeps the last appeal link visible', async () => {
  const first = deferred<Response>();
  const second = deferred<Response>();
  const third = deferred<Response>();
  const fetcher = vi.fn<typeof fetch>().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise).mockReturnValueOnce(third.promise);
  vi.stubGlobal('fetch', fetcher);
  const show = setup(provider, '1');
  await act(async () => fireEvent.click(screen.getByText('Team A')));

  for (const version of ['2', '3', '4', '5', '6']) show('t', version);
  expect(fetcher).toHaveBeenCalledTimes(1);
  expect(fetcher.mock.calls[0]![1]?.signal?.aborted).toBe(false);

  await act(async () => first.resolve(jsonResponse(mine('1'))));
  const appealLink = () => screen.getByRole('link', { name: 'Перейти к апелляции' });
  expect(appealLink()).toHaveAttribute('href', '/appeals?tournamentId=t&resultId=r&expectedResultVersion=1');
  expect(fetcher).toHaveBeenCalledTimes(2);

  for (const version of ['7', '8', '9']) show('t', version);
  expect(appealLink()).toBeVisible();
  expect(fetcher).toHaveBeenCalledTimes(2);
  expect(fetcher.mock.calls[1]![1]?.signal?.aborted).toBe(false);

  await act(async () => second.resolve(jsonResponse(mine('2'))));
  expect(appealLink()).toHaveAttribute('href', '/appeals?tournamentId=t&resultId=r&expectedResultVersion=2');
  expect(fetcher).toHaveBeenCalledTimes(3);
  await act(async () => third.resolve(jsonResponse({}, 503)));
  expect(screen.getByRole('alert')).toBeVisible();
  expect(appealLink()).toHaveAttribute('href', '/appeals?tournamentId=t&resultId=r&expectedResultVersion=2');
  expect(fetcher).toHaveBeenCalledTimes(3);
});

it('clears the old tournament, cancels its reading and drops its queued refresh', async () => {
  const previous = deferred<Response>();
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(jsonResponse(mine()))
    .mockReturnValueOnce(previous.promise).mockResolvedValueOnce(jsonResponse({ tournamentId: 'other', results: [] }));
  vi.stubGlobal('fetch', fetcher);
  const show = setup(provider, '1');
  fireEvent.click(screen.getByText('Team A'));
  await screen.findByRole('link');
  show('t', '2');
  show('t', '3');
  show('other', '1');
  expect(screen.queryByRole('link')).toBeNull();
  expect(fetcher.mock.calls[1]![1]?.signal?.aborted).toBe(true);
  await screen.findByText(/Опубликованных результатов пока нет/);
  await act(async () => previous.resolve(jsonResponse(mine('999'))));
  expect(screen.queryByRole('link')).toBeNull();
  expect(fetcher.mock.calls.map(([url]) => url)).toEqual([
    '/api/v1/results/me/tournaments/t/results',
    '/api/v1/results/me/tournaments/t/results',
    '/api/v1/results/me/tournaments/other/results',
  ]);
});

it('immediately removes old private content at subject switch and rejects late previous-user response', async () => {
  const late = deferred<Response>(); const nextIdentity = deferred<SessionCredential>();
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(jsonResponse(mine()))
    .mockReturnValueOnce(late.promise).mockResolvedValueOnce(jsonResponse({ tournamentId: 't', results: [] }));
  vi.stubGlobal('fetch', fetcher);
  const show = setup({ ...provider, select: key => key === 'b' ? nextIdentity.promise : provider.select(key, new AbortController().signal) }, '1');
  fireEvent.click(screen.getByText('Team A')); await screen.findByRole('link');
  fireEvent.click(screen.getByText('Обновить мои результаты'));
  show('t', '2');
  fireEvent.click(screen.getByText('Team B'));
  expect(screen.queryByRole('link')).toBeNull();
  expect(fetcher.mock.calls[1]![1]?.signal?.aborted).toBe(true);
  await act(async () => late.resolve(jsonResponse(mine('999')))); expect(screen.queryByRole('link')).toBeNull();
  await act(async () => nextIdentity.resolve(await provider.select('b', new AbortController().signal)));
  expect(await screen.findByText(/Опубликованных результатов пока нет/)).toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledTimes(3);
  expect(new Headers(fetcher.mock.calls[2]![1]?.headers).get('Authorization')).toBe('Bearer memory-secret-b');
  fireEvent.click(screen.getByText('Logout')); expect(screen.queryByRole('region')).toBeNull();
});
it.each(['privateField', 'wrongTournament', 'numericVersion', 'duplicateTask'])('rejects malformed private DTO: %s', variant => {
  const value: any = mine();
  if (variant === 'privateField') value.results[0].capability = 'private';
  if (variant === 'wrongTournament') value.tournamentId = 'other';
  if (variant === 'numericVersion') value.results[0].resultVersion = 42;
  if (variant === 'duplicateTask') value.results.push(value.results[0]);
  expect(() => readTeamResults(value, 't')).toThrow();
});

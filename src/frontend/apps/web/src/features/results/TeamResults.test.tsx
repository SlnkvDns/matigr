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
  ...credential(key).profile, roles: ['Team'], teamId: key, tournamentIds: ['t'] } }) };
function Switch() {
  const { session } = useSession();
  return <><button onClick={() => void session.select('a')}>Team A</button><button onClick={() => void session.select('b')}>Team B</button>
    <button onClick={() => session.signOut()}>Logout</button></>;
}
function setup(identity = provider) {
  return render(<SessionProvider provider={identity}><MemoryRouter><Switch /><TeamResults tournament="t" /></MemoryRouter></SessionProvider>);
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
it('immediately removes old private content at subject switch and rejects late previous-user response', async () => {
  const late = deferred<Response>(); const nextIdentity = deferred<SessionCredential>();
  const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse(mine())).mockReturnValueOnce(late.promise).mockResolvedValue(jsonResponse({ tournamentId: 't', results: [] }));
  vi.stubGlobal('fetch', fetcher);
  setup({ ...provider, select: key => key === 'b' ? nextIdentity.promise : provider.select(key, new AbortController().signal) });
  fireEvent.click(screen.getByText('Team A')); await screen.findByRole('link');
  fireEvent.click(screen.getByText('Обновить мои результаты')); fireEvent.click(screen.getByText('Team B'));
  expect(screen.queryByRole('link')).toBeNull();
  await act(async () => late.resolve(jsonResponse(mine('999')))); expect(screen.queryByRole('link')).toBeNull();
  await act(async () => nextIdentity.resolve(await provider.select('b', new AbortController().signal)));
  expect(await screen.findByText(/Опубликованных результатов пока нет/)).toBeInTheDocument();
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

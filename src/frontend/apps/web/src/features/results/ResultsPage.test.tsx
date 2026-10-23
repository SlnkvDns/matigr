import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { expect, it, vi } from 'vitest';
import { ResultsPage } from './ResultsPage';
import { readStandings } from './standings';
import { jsonResponse } from '../../shared/auth/test-support';

export const table = () => ({ tournamentId: 'tournament', snapshotVersion: '9007199254740993', tasks: [{ taskId: 'task', name: 'Задача' }],
  teams: [{ teamId: 'a', name: 'Альфа', cells: [{ status: 'Published', score: '10' }], total: '10', rank: 1 },
    { teamId: 'b', name: 'Бета', cells: [{ status: 'Published', score: '10' }], total: '10', rank: 1 },
    { teamId: 'c', name: 'Гамма', cells: [{ status: 'Pending' }], total: '0', rank: 2 }] });

it('renders server scores, shared ranks and pending anonymously without authorizing appeals', async () => {
  const fetcher = vi.fn(async (_url: RequestInfo | URL, _init?: RequestInit) => jsonResponse(table())); vi.stubGlobal('fetch', fetcher);
  render(<MemoryRouter initialEntries={['/results?tournamentId=tournament']}><ResultsPage /></MemoryRouter>);
  const grid = await screen.findByRole('table');
  expect(within(grid).getByRole('row', { name: /2 Гамма/ })).toHaveTextContent('—0');
  expect(within(grid).getAllByRole('row', { name: /^1 / })).toHaveLength(2);
  expect(screen.queryByText(/Подать апелляцию/)).toBeNull();
  const init = fetcher.mock.calls[0]![1] as RequestInit;
  expect(new Headers(init.headers).has('Authorization')).toBe(false); expect(init.credentials).toBe('omit');
});
it('uses server totals/ranks even when they differ from a browser guess', async () => {
  const data = table(); data.teams[0]!.total = '9007199254740993'; data.teams[0]!.rank = 42;
  vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(data)));
  render(<MemoryRouter initialEntries={['/results?tournamentId=tournament']}><ResultsPage /></MemoryRouter>);
  expect(await screen.findByRole('row', { name: /42 Альфа/ })).toHaveTextContent('9007199254740993');
});
it('shows dependency failure and allows an explicit public reload', async () => {
  const fetcher = vi.fn().mockRejectedValueOnce(new TypeError('offline')).mockResolvedValueOnce(jsonResponse(table())); vi.stubGlobal('fetch', fetcher);
  render(<MemoryRouter initialEntries={['/results?tournamentId=tournament']}><ResultsPage /></MemoryRouter>);
  await screen.findByRole('alert'); fireEvent.click(screen.getByText('Обновить таблицу')); await screen.findByRole('table');
});
it.each(['private', 'pendingScore', 'wrongCells', 'impreciseVersion'])('rejects invalid DTO: %s', variant => {
  const value: any = table();
  if (variant === 'private') value.capability = 'secret';
  if (variant === 'pendingScore') value.teams[2].cells[0].score = '0';
  if (variant === 'wrongCells') value.teams[0].cells = [];
  if (variant === 'impreciseVersion') value.snapshotVersion = 9007199254740993;
  expect(() => readStandings(value, 'tournament')).toThrow();
});

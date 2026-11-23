import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppRoutes } from './AppRoutes';
import { SessionProvider } from '../shared/auth/SessionContext';
import type { IdentityProvider } from '../shared/auth/identity';
import { credential, jsonResponse } from '../shared/auth/test-support';
import { demoTournamentId } from '../shared/tournament';

function renderApp(path = '/', provider?: IdentityProvider) {
  return render(<SessionProvider provider={provider}><MemoryRouter initialEntries={[path]}><AppRoutes /></MemoryRouter></SessionProvider>);
}

describe('single application navigation', () => {
  beforeEach(() => {
    vi.stubEnv('DEV', false);
    vi.stubEnv('VITE_TOURNAMENT_ID', undefined);
  });
  afterEach(() => vi.unstubAllEnvs());

  it('opens the public table route without issuing private requests or reading credentials', () => {
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    renderApp();
    expect(screen.getByRole('heading', { level: 1, name: 'Турнирная таблица' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Таблица' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByText(/Таблица доступна всем, без входа/)).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('switches feature routes, active link, document title and heading focus', async () => {
    const user = userEvent.setup();
    renderApp('/results');
    await user.click(screen.getByRole('link', { name: 'Проверка' }));
    const checking = screen.getByRole('heading', { level: 1, name: 'Проверка решений' });
    expect(checking).toHaveFocus();
    expect(document.title).toBe('Проверка решений · Матигр');
    expect(screen.getByRole('link', { name: 'Проверка' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Таблица' })).not.toHaveAttribute('aria-current');
    await user.click(screen.getByRole('link', { name: 'Апелляции' }));
    expect(screen.getByRole('heading', { level: 1, name: 'Апелляции' })).toBeInTheDocument();
    expect(document.title).toBe('Апелляции · Матигр');
  });

  it('keeps a manually selected tournament when switching features and roles', async () => {
    const tournamentId = 'review-tournament';
    const user = userEvent.setup();
    const fetch = vi.fn(async (url: string) => {
      if (url.endsWith('/standings')) return jsonResponse({ tournamentId, snapshotVersion: '0', tasks: [], teams: [] });
      if (url.endsWith('/me/assignment')) return new Response(null, { status: 204 });
      if (url.endsWith('/results')) return jsonResponse({ tournamentId, results: [] });
      return jsonResponse({ tournamentId, items: [], nextCursor: null });
    });
    vi.stubGlobal('fetch', fetch);
    renderApp('/results', {
      choices: [{ key: 'jury', label: 'Жюри' }, { key: 'team', label: 'Команда' }],
      select: async key => {
        const identity = credential(key);
        return {
          ...identity,
          profile: {
            ...identity.profile, tournamentIds: [tournamentId],
            ...(key === 'team' ? { roles: ['Team'], teamId: 'team-a' } : {}),
          },
        };
      },
    });

    await user.type(screen.getByRole('textbox', { name: 'Идентификатор турнира' }), tournamentId);
    await user.click(screen.getByRole('button', { name: 'Показать таблицу' }));
    await screen.findByRole('table');
    const selector = screen.getByRole('combobox', { name: 'Тестовый пользователь' });
    await user.selectOptions(selector, 'jury');
    await user.click(screen.getByRole('button', { name: 'Выбрать' }));
    await user.click(screen.getByRole('link', { name: 'Проверка' }));
    await screen.findByRole('heading', { name: 'Ваша проверка' });
    await user.click(screen.getByRole('link', { name: 'Апелляции' }));
    await user.selectOptions(selector, 'team');
    await user.click(screen.getByRole('button', { name: 'Выбрать' }));
    await user.click(screen.getByRole('link', { name: 'Таблица' }));

    expect(screen.getByRole('textbox', { name: 'Идентификатор турнира' })).toHaveValue(tournamentId);
    expect(await screen.findByRole('table')).toBeInTheDocument();
    expect(await screen.findByText('Опубликованных результатов пока нет. Ожидайте завершения проверки.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Выйти' }));
    expect(screen.queryByRole('heading', { name: 'Результаты вашей команды' })).not.toBeInTheDocument();
    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Идентификатор турнира' })).toHaveValue(tournamentId);
  });

  it.each([
    ['/', undefined, demoTournamentId],
    ['/results', 'configured', 'configured'],
    ['/?tournamentId=explicit', 'configured', 'explicit'],
  ])('loads the local table from %s with configured tournament %s', async (path, configured, tournamentId) => {
    vi.stubEnv('DEV', true);
    vi.stubEnv('VITE_TOURNAMENT_ID', configured);
    const fetch = vi.fn(async () => jsonResponse({ tournamentId, snapshotVersion: '0', tasks: [], teams: [] }));
    vi.stubGlobal('fetch', fetch);
    renderApp(path);
    expect(screen.getByRole('textbox', { name: 'Идентификатор турнира' })).toHaveValue(tournamentId);
    expect(await screen.findByRole('table')).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledWith(`/api/v1/results/tournaments/${tournamentId}/standings`, expect.anything());
  });

  it('carries only the public tournament identifier from an appeal link', () => {
    renderApp('/appeals?tournamentId=selected&resultId=result&expectedResultVersion=3');
    expect(screen.getByRole('link', { name: 'Таблица' })).toHaveAttribute('href', '/results?tournamentId=selected');
    expect(screen.getByRole('link', { name: 'Проверка' })).toHaveAttribute('href', '/checking?tournamentId=selected');
    expect(screen.getByRole('link', { name: 'Апелляции' })).toHaveAttribute('href', '/appeals?tournamentId=selected');
  });

  it.each([
    ['/results', 'Турнирная таблица'], ['/checking', 'Проверка решений'], ['/appeals', 'Апелляции'],
  ])('supports direct navigation to %s without fake results, controls or assignments', (path, heading) => {
    renderApp(path);
    expect(screen.getByRole('heading', { level: 1, name: heading })).toBeInTheDocument();
    expect(screen.getByText(path === '/checking' ? 'Для проверки войдите как член жюри.' : path === '/results' ? 'Таблица доступна всем, без входа.' : 'Для подачи апелляции войдите как команда.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    if (path !== '/results') {
      expect(screen.queryByRole('button')).not.toBeInTheDocument();
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    }
    expect(screen.getByRole('link', { name: 'К содержимому' })).toHaveAttribute('href', '#main');
  });

  it('shows a real not-found page with a working return link', async () => {
    const user = userEvent.setup();
    renderApp('/unknown');
    expect(screen.getByRole('heading', { level: 1, name: 'Страница не найдена' })).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: /К таблице/ }));
    expect(screen.getByRole('heading', { level: 1, name: 'Турнирная таблица' })).toBeInTheDocument();
  });
});

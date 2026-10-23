import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { AppRoutes } from './AppRoutes';
import { SessionProvider } from '../shared/auth/SessionContext';

function renderApp(path = '/') {
  return render(<SessionProvider><MemoryRouter initialEntries={[path]}><AppRoutes /></MemoryRouter></SessionProvider>);
}

describe('single application navigation', () => {
  it('opens the public table route without issuing private requests or reading credentials', () => {
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    renderApp();
    expect(screen.getByRole('heading', { level: 1, name: 'Турнирная таблица' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Таблица' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByText(/Таблица будет доступна всем, без входа/)).toBeInTheDocument();
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

  it.each([
    ['/results', 'Турнирная таблица'], ['/checking', 'Проверка решений'], ['/appeals', 'Апелляции'],
  ])('supports direct navigation to %s without fake results, controls or assignments', (path, heading) => {
    renderApp(path);
    expect(screen.getByRole('heading', { level: 1, name: heading })).toBeInTheDocument();
    expect(screen.getByText(path === '/checking' ? 'Для проверки войдите как член жюри.' : 'Раздел готовится')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
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

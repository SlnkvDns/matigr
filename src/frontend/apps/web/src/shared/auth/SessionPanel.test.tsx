import { StrictMode, useState } from 'react';
import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { AppRoutes } from '../../app/AppRoutes';
import { PrivateSessionBoundary, SessionProvider, useSession } from './SessionContext';
import { SessionPanel } from './SessionPanel';
import { createDevIdentityProvider } from './identity';
import { deferred, jsonResponse, wireSession } from './test-support';

describe('dev selection and server-approved profile', () => {
  it('works in StrictMode, keeps the table anonymous, shows the server profile, and never renders or persists the token', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(jsonResponse({ ...wireSession(), profile: {
      subjectId: 'server-subject', displayName: 'Альфа', roles: ['Team'], tournamentIds: ['server-tournament'], teamId: 'server-team',
    } }));
    vi.stubGlobal('fetch', fetch);
    const setStorage = vi.spyOn(Storage.prototype, 'setItem');
    const user = userEvent.setup();
    const view = render(<StrictMode><SessionProvider provider={createDevIdentityProvider()}>
      <MemoryRouter initialEntries={['/results']}><AppRoutes /></MemoryRouter>
    </SessionProvider></StrictMode>);
    expect(screen.getByText('Гость')).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
    await user.selectOptions(screen.getByLabelText('Тестовый пользователь'), 'senior');
    await user.click(screen.getByRole('button', { name: 'Выбрать' }));
    expect(await screen.findByText('Альфа')).toBeInTheDocument();
    expect(screen.getByText('Команда', { exact: true })).toBeInTheDocument();
    expect(screen.queryByText('Старший жюри', { selector: '.session-description' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Турнирная таблица' })).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(view.container.innerHTML).not.toContain('memory-secret');
    expect(setStorage).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Выйти' }));
    expect(screen.getByText('Гость')).toBeInTheDocument();
    expect(screen.queryByText('Альфа')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Турнирная таблица' })).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('resets private component drafts/actions at switch start and preserves public component state', async () => {
    const pending = deferred<Response>();
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(jsonResponse(wireSession('jury-a')))
      .mockReturnValueOnce(pending.promise);
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    function PrivateProbe() {
      const { snapshot } = useSession();
      const [draft, setDraft] = useState('');
      return <section aria-label="Частное состояние">
        {snapshot.profile && <><input aria-label="Черновик действия" value={draft} onChange={event => setDraft(event.target.value)} />
          <span>Материал {snapshot.profile.displayName}</span></>}
      </section>;
    }
    function PublicProbe() {
      const [count, setCount] = useState(0);
      return <button onClick={() => setCount(count + 1)}>Публичная таблица: {count}</button>;
    }
    render(<SessionProvider provider={createDevIdentityProvider()}><SessionPanel /><PublicProbe />
      <PrivateSessionBoundary><PrivateProbe /></PrivateSessionBoundary></SessionProvider>);
    await user.click(screen.getByRole('button', { name: 'Публичная таблица: 0' }));
    await user.selectOptions(screen.getByLabelText('Тестовый пользователь'), 'jury-a');
    await user.click(screen.getByRole('button', { name: 'Выбрать' }));
    await user.type(await screen.findByLabelText('Черновик действия'), 'старое действие');
    await user.selectOptions(screen.getByLabelText('Тестовый пользователь'), 'jury-b');
    await user.click(screen.getByRole('button', { name: 'Выбрать' }));
    expect(screen.getByText('Выполняется вход…')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Частное состояние' })).queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.queryByText('Материал jury-a')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Публичная таблица: 1' })).toBeInTheDocument();
    await act(async () => pending.resolve(jsonResponse(wireSession('jury-b'))));
    expect(await screen.findByText('Материал jury-b')).toBeInTheDocument();
    expect(screen.getByLabelText('Черновик действия')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Публичная таблица: 1' })).toBeInTheDocument();
  });

  it('reports a failed selection safely and retries only on a deliberate action', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(new Response(JSON.stringify({
      type: 'about:blank', title: 'Unavailable', status: 503, code: 'Unavailable', correlationId: 'request-1', detail: 'private-server-detail',
    }), { status: 503, headers: { 'Content-Type': 'application/problem+json' } })).mockResolvedValueOnce(jsonResponse(wireSession()));
    vi.stubGlobal('fetch', fetch);
    const user = userEvent.setup();
    render(<SessionProvider provider={createDevIdentityProvider()}><SessionPanel /></SessionProvider>);
    await user.selectOptions(screen.getByLabelText('Тестовый пользователь'), 'jury-a');
    await user.click(screen.getByRole('button', { name: 'Выбрать' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Сервис временно недоступен');
    expect(screen.getByRole('alert')).toHaveTextContent('request-1');
    expect(screen.queryByText(/private-server-detail/)).not.toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Гость')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Выбрать' }));
    expect(await screen.findByText('jury-a')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('has no dev selector or network request when no identity provider is configured', () => {
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    render(<SessionProvider><SessionPanel /></SessionProvider>);
    expect(screen.getByText('Гость')).toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });
});

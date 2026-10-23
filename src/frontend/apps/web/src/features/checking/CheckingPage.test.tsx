import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { SessionProvider, PrivateSessionBoundary, useSession } from '../../shared/auth/SessionContext';
import { credential, jsonResponse } from '../../shared/auth/test-support';
import { CheckingPage } from './CheckingPage';

afterEach(() => vi.unstubAllGlobals());
function Login() {
  const { session } = useSession();
  return <button onClick={() => void session.select('jury')}>Войти</button>;
}
async function setup(state = 'Assigned', lost = false) {
  let assigned = true;
  const posts: RequestInit[] = [];
  const assignment = { check: { checkId: 'check', taskId: 'task', teamId: 'team', state, version: '2' }, assignmentId: 'assignment', capability: 'private-secret' };
  const fetcher = vi.fn(async (url: string, init: RequestInit) => {
    if (init.method === 'POST') {
      posts.push(init);
      assigned = false;
      if (lost && posts.length === 1) throw new TypeError('lost reply');
      return jsonResponse({ state: 'Completed' });
    }
    if (url.endsWith('/material')) return new Response('<script>escaped</script>', { headers: { 'Content-Type': 'text/plain' } });
    if (url.includes('/queues?')) return jsonResponse({ items: [], nextCursor: null });
    if (!assigned) return new Response(null, { status: 204 });
    return new Response(JSON.stringify(assignment), { headers: { 'Content-Type': 'application/json', ETag: '"2"' } });
  });
  vi.stubGlobal('fetch', fetcher);
  render(<SessionProvider provider={{ choices: [], select: async () => credential() }}><Login /><PrivateSessionBoundary><CheckingPage /></PrivateSessionBoundary></SessionProvider>);
  fireEvent.click(screen.getByText('Войти'));
  await screen.findByText(state === 'SeniorAssigned' ? 'Окончательный вердикт старшего' : 'Назначена вам');
  return { posts, fetcher };
}

it('reads only owner material as escaped text and completes with server ETag/capability', async () => {
  const { posts } = await setup();
  fireEvent.click(screen.getByText('Открыть материал'));
  expect(await screen.findByLabelText('Материал решения')).toHaveTextContent('<script>escaped</script>');
  expect(document.querySelector('script')).toBeNull();
  fireEvent.click(screen.getByText('Правильно'));
  await screen.findByText('Вердикт сохранён. Результат ожидает обработки.');
  await waitFor(() => expect(screen.getByText('Взять следующую')).toBeEnabled());
  expect(new Headers(posts[0]!.headers).get('If-Match')).toBe('"2"');
  expect(JSON.parse(posts[0]!.body as string)).toEqual({ assignmentId: 'assignment', capability: 'private-secret', outcome: 'Correct' });
  expect(document.body.textContent).not.toContain('private-secret');
  expect(screen.queryByLabelText('Материал решения')).toBeNull();
});

it('refusal releases work and offers next; senior has only final binary actions', async () => {
  await setup();
  fireEvent.click(screen.getByText('Передать старшему'));
  await screen.findByText('Проверка передана в очередь старшего. Вы свободны.');
  await waitFor(() => expect(screen.getByText('Взять следующую')).toBeEnabled());
});
it('senior assignment cannot refuse again', async () => {
  await setup('SeniorAssigned');
  expect(screen.queryByText('Передать старшему')).toBeNull();
  expect(screen.getByText('Неправильно')).toBeEnabled();
});
it('ambiguous reply prevents a new action and explicit retry keeps key/payload/version', async () => {
  const { posts } = await setup('Assigned', true);
  fireEvent.click(screen.getByText('Правильно'));
  fireEvent.click(await screen.findByText('Повторить прежний запрос'));
  await screen.findByText('Вердикт сохранён. Результат ожидает обработки.');
  expect(posts).toHaveLength(2);
  expect(posts[0]!.body).toEqual(posts[1]!.body);
  expect(new Headers(posts[0]!.headers).get('Idempotency-Key')).toEqual(new Headers(posts[1]!.headers).get('Idempotency-Key'));
});

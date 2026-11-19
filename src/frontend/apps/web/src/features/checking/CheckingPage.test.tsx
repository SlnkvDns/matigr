import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { SessionProvider, PrivateSessionBoundary, useSession } from '../../shared/auth/SessionContext';
import { credential, deferred, jsonResponse } from '../../shared/auth/test-support';
import { CheckingPage } from './CheckingPage';

afterEach(() => vi.unstubAllGlobals());
function Login() {
  const { session } = useSession();
  return <button onClick={() => void session.select('jury')}>Войти</button>;
}
const demoTournament = '0344e06b-4ea1-582e-bd91-e832719773f2';
const demoTaskOne = 'acdc6594-8ac1-5f18-bb65-def577a79950';
const textResponse = (text: string) => new Response(text, { headers: { 'Content-Type': 'text/plain' } });
function placeholder(taskNumber: number) {
  return textResponse(`Подготовленное принятое решение.\nКоманда: Альфа\nЗадача ${taskNumber}\nМатериал тестового профиля Matigr v1; доступен только назначенному жюри.\n`);
}
async function setup({ state = 'Assigned', lost = false, task = { tournamentId: 'tournament', taskId: 'task' },
  readMaterial = () => textResponse('<script>escaped</script>'), waitForMaterial = true }: {
  state?: string; lost?: boolean; task?: { tournamentId: string; taskId: string };
  readMaterial?: () => Response | Promise<Response>; waitForMaterial?: boolean;
} = {}) {
  let assigned = true;
  const posts: RequestInit[] = [];
  const assignment = { check: { checkId: 'check', ...task, teamId: 'team', state, version: '2' }, assignmentId: 'assignment', capability: 'private-secret' };
  const fetcher = vi.fn(async (url: string, init: RequestInit) => {
    if (init.method === 'POST') {
      posts.push(init);
      assigned = false;
      if (lost && posts.length === 1) throw new TypeError('lost reply');
      return jsonResponse({ state: 'Completed' });
    }
    if (url.endsWith('/material')) return readMaterial();
    if (url.includes('/queues?')) return jsonResponse({ items: [], nextCursor: null });
    if (!assigned) return new Response(null, { status: 204 });
    return new Response(JSON.stringify(assignment), { headers: { 'Content-Type': 'application/json', ETag: '"2"' } });
  });
  vi.stubGlobal('fetch', fetcher);
  render(<SessionProvider provider={{ choices: [], select: async () => credential() }}><Login /><PrivateSessionBoundary><CheckingPage /></PrivateSessionBoundary></SessionProvider>);
  fireEvent.click(screen.getByText('Войти'));
  await screen.findByText(state === 'SeniorAssigned' ? 'Назначена вам из очереди старшего жюри' : 'Назначена вам');
  if (waitForMaterial) await screen.findByLabelText('Материал решения');
  return { posts, fetcher };
}

it.each([
  [demoTaskOne, 1, /двузначные натуральные числа/, '32 и 67'],
  ['11bf5e9e-d65b-5d67-b25f-ada9c2cfbcff', 2, /Из чисел от 1 до 20/, 'нечётная часть'],
])('shows the matching condition and example after receiving the legacy demo material for task %s', async (taskId, number, statement, example) => {
  const { fetcher } = await setup({ task: { tournamentId: demoTournament, taskId }, readMaterial: () => placeholder(number) });
  expect(screen.getByRole('heading', { name: 'Учебное условие' })).toBeVisible();
  expect(screen.getByText(statement)).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Учебный пример решения' })).toBeVisible();
  expect(screen.getByLabelText('Материал решения')).toHaveTextContent(example);
  expect(screen.getByLabelText('Материал решения')).not.toHaveTextContent('Команда: Альфа');
  expect(fetcher.mock.calls.some(([url, init]) =>
    url.endsWith('/checks/check/material') && new Headers(init.headers).has('Authorization'))).toBe(true);
});

it('uses demo presentation only for the designated tournament and the original placeholder text', async () => {
  await setup({ task: { tournamentId: 'another-tournament', taskId: demoTaskOne }, readMaterial: () => placeholder(1) });
  expect(screen.queryByRole('heading', { name: 'Учебное условие' })).toBeNull();
  expect(screen.getByLabelText('Материал решения')).toHaveTextContent('Команда: Альфа');
});

it('keeps the submitted solution if the demo task has real material', async () => {
  await setup({ task: { tournamentId: demoTournament, taskId: demoTaskOne }, readMaterial: () => textResponse('Текст участника.') });
  expect(screen.getByRole('heading', { name: 'Решение команды' })).toBeVisible();
  expect(screen.getByLabelText('Материал решения')).toHaveTextContent('Текст участника.');
});

it('loads the solution automatically, shows no verdict during a failed read, and allows retry or transfer', async () => {
  const delayed = deferred<Response>();
  const readMaterial = vi.fn().mockReturnValueOnce(delayed.promise).mockImplementation(() => textResponse('Решение загружено.'));
  const { posts } = await setup({ readMaterial, waitForMaterial: false });
  expect(await screen.findByText('Загружаем решение…')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Правильно' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Неправильно' })).toBeNull();
  expect(screen.getByRole('button', { name: 'Передать старшему' })).toBeEnabled();
  await act(async () => delayed.resolve(new Response(null, { status: 503 })));
  expect(await screen.findByRole('alert')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Правильно' })).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку решения' }));
  expect(await screen.findByLabelText('Материал решения')).toHaveTextContent('Решение загружено.');
  expect(screen.getByRole('button', { name: 'Правильно' })).toBeEnabled();
  expect(posts).toHaveLength(0);
});

it('reads only owner material as escaped text and completes with server ETag/capability', async () => {
  const { posts } = await setup();
  expect(await screen.findByLabelText('Материал решения')).toHaveTextContent('<script>escaped</script>');
  expect(document.querySelector('script')).toBeNull();
  fireEvent.click(screen.getByText('Правильно'));
  await screen.findByText('Вердикт сохранён. Результаты обновляются автоматически.');
  await waitFor(() => expect(screen.getByText('Взять следующую')).toBeEnabled());
  expect(new Headers(posts[0]!.headers).get('If-Match')).toBe('"2"');
  expect(JSON.parse(posts[0]!.body as string)).toEqual({ assignmentId: 'assignment', capability: 'private-secret', outcome: 'Correct' });
  expect(document.body.textContent).not.toContain('private-secret');
  expect(screen.queryByLabelText('Материал решения')).toBeNull();
});

it('refusal releases work and offers next; senior has only final binary actions', async () => {
  await setup();
  fireEvent.click(screen.getByText('Передать старшему'));
  await screen.findByText('Проверка передана в очередь старшего.');
  await waitFor(() => expect(screen.getByText('Взять следующую')).toBeEnabled());
});
it('senior assignment cannot refuse again', async () => {
  await setup({ state: 'SeniorAssigned' });
  expect(screen.queryByText('Передать старшему')).toBeNull();
  expect(screen.getByText('Неправильно')).toBeEnabled();
});
it('ambiguous reply prevents a new action and explicit retry keeps key/payload/version', async () => {
  const { posts } = await setup({ lost: true });
  fireEvent.click(screen.getByText('Правильно'));
  fireEvent.click(await screen.findByText('Повторить прежний запрос'));
  await screen.findByText('Вердикт сохранён. Результаты обновляются автоматически.');
  expect(posts).toHaveLength(2);
  expect(posts[0]!.body).toEqual(posts[1]!.body);
  expect(new Headers(posts[0]!.headers).get('Idempotency-Key')).toEqual(new Headers(posts[1]!.headers).get('Idempotency-Key'));
});

it('online recovery ignores an old assignment response that arrives after the newer empty read', async () => {
  const old = deferred<Response>(); let reads = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    if (url.endsWith('/me/assignment')) { ++reads; return reads === 1 ? old.promise : new Response(null, { status: 204 }); }
    return jsonResponse({ items: [], nextCursor: null });
  }));
  render(<SessionProvider provider={{ choices: [], select: async () => credential() }}><Login /><PrivateSessionBoundary><CheckingPage /></PrivateSessionBoundary></SessionProvider>);
  fireEvent.click(screen.getByText('Войти')); await waitFor(() => expect(reads).toBe(1));
  fireEvent(window, new Event('online')); await screen.findByText('У вас нет незавершённой проверки.');
  old.resolve(jsonResponse({ check: { checkId: 'old', state: 'Assigned', taskId: 'stale', teamId: 'team' }, assignmentId: 'old', capability: 'secret' }));
  await waitFor(() => expect(screen.getByText('Взять следующую')).toBeEnabled());
  expect(screen.queryByText('Назначена вам')).toBeNull(); expect(document.body.textContent).not.toContain('stale');
});

it('another tab invalidates current work and late material cannot return to the page', async () => {
  class Channel {
    static instances: Channel[] = []; onmessage: ((event: MessageEvent) => void) | null = null;
    constructor() { Channel.instances.push(this); }
    postMessage = vi.fn(); close = vi.fn();
  }
  vi.stubGlobal('BroadcastChannel', Channel);
  const material = deferred<Response>();
  const { fetcher } = await setup({ readMaterial: () => material.promise, waitForMaterial: false });
  fetcher.mockImplementation(async (url: string) => {
    if (url.endsWith('/material')) return material.promise;
    if (url.endsWith('/me/assignment')) return new Response(null, { status: 204 });
    return jsonResponse({ items: [], nextCursor: null });
  });
  Channel.instances.at(-1)!.onmessage?.(new MessageEvent('message', { data: { type: 'changed' } }));
  await screen.findByText('У вас нет незавершённой проверки.');
  await act(async () => material.resolve(textResponse('late private bytes')));
  await waitFor(() => expect(screen.getByText('Взять следующую')).toBeEnabled());
  expect(screen.queryByLabelText('Материал решения')).toBeNull(); expect(document.body.textContent).not.toContain('late private bytes');
});

it('412 reconciles assignment without resubmitting a verdict under a new key', async () => {
  const { fetcher } = await setup(); let posts = 0;
  fetcher.mockImplementation(async (_url: string, init: RequestInit) => {
    if (init.method === 'POST') { ++posts; return new Response(JSON.stringify({ type: 'about:blank', title: 'Conflict', status: 412, code: 'CheckVersionConflict', correlationId: 'ref' }), { status: 412, headers: { 'Content-Type': 'application/problem+json' } }); }
    if (_url.endsWith('/me/assignment')) return new Response(null, { status: 204 });
    return jsonResponse({ items: [], nextCursor: null });
  });
  fireEvent.click(screen.getByText('Правильно')); await screen.findByRole('alert');
  await screen.findByText('У вас нет незавершённой проверки.'); expect(posts).toBe(1);
  expect(screen.queryByText('Повторить прежний запрос')).toBeNull();
});

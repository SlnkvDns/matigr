import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ApiError, ApiProtocolError } from '../../shared/api/http';
import { useSession } from '../../shared/auth/SessionContext';
import { ApiProblemNotice } from '../../shared/ui/ApiProblemNotice';
import { Page } from '../../shared/ui/Page';
import { exactVersion, readList, readView, states } from './appeals';
import type { AppealSummary, AppealView } from './appeals';
import { JuryAppeals } from './JuryAppeals';

export function AppealsPage() {
  const { snapshot } = useSession(); const [query] = useSearchParams();
  const tournament = query.get('tournamentId') ?? snapshot.profile?.tournamentIds[0] ?? '';
  return <Page title="Апелляции" description="Обращения команд по опубликованным результатам.">
    {snapshot.profile?.roles.includes('Team') ? <TeamAppeals tournament={tournament} />
      : snapshot.profile?.roles.some(r => r === 'Jury' || r === 'Senior') ? <JuryAppeals tournament={tournament} /> : <p>Для подачи апелляции войдите как команда.</p>}
  </Page>;
}
function TeamAppeals({ tournament }: { tournament: string }) {
  const { session, snapshot } = useSession(); const team = snapshot.profile!.teamId!;
  const [query] = useSearchParams(); const resultId = query.get('resultId') ?? ''; const version = query.get('expectedResultVersion') ?? '';
  const [items, setItems] = useState<AppealSummary[]>([]); const [existing, setExisting] = useState<AppealView>();
  const [details, setDetails] = useState<AppealView[]>([]);
  const latest = useRef(new Map<string, AppealView>());
  const [error, setError] = useState<unknown>(); const [loading, setLoading] = useState(false); const [sending, setSending] = useState(false);
  const [ready, setReady] = useState(false); const [retry, setRetry] = useState(false); const [notice, setNotice] = useState(''); const [blocked, setBlocked] = useState(false);
  const sequence = useRef(0); const reads = useRef<AbortController | undefined>(undefined); const action = useRef<AbortController | undefined>(undefined);
  const pending = useRef<{ resultId: string; expectedResultVersion: string; key: string } | undefined>(undefined); const inFlight = useRef(false);
  async function load(preserveError = false) {
    const generation = ++sequence.current; reads.current?.abort(); const abort = new AbortController(); reads.current = abort;
    setLoading(true); setReady(false); if (!preserveError) setError(undefined);
    try {
      const client = session.workspace().client('/api/v1/appeals');
      const response = await client.get<unknown>(`/me?${new URLSearchParams({ tournamentId: tournament })}`, { signal: abort.signal });
      const list = readList(response.data, tournament, team);
      const details = await Promise.all(list.map(async item => readView((await client.get<unknown>(`/${encodeURIComponent(item.appealId)}`, { signal: abort.signal })).data, tournament)));
      if (generation !== sequence.current || abort.signal.aborted) return;
      if (details.some(d => d.teamId !== team)) throw new ApiProtocolError();
      const stable = details.map(d => { const old = latest.current.get(d.appealId); const value = old && BigInt(old.version) > BigInt(d.version) ? old : d; latest.current.set(d.appealId, value); return value; });
      setItems(list.map(i => { const d = latest.current.get(i.appealId)!; return { ...i, state: d.state, version: d.version }; })); setDetails(stable); setExisting(stable.find(d => d.resultId === resultId)); setReady(true);
    } catch (e) { if (generation === sequence.current && !abort.signal.aborted) setError(e); }
    finally { if (generation === sequence.current && !abort.signal.aborted) setLoading(false); }
  }
  useEffect(() => {
    setExisting(undefined); setItems([]); setDetails([]); latest.current.clear(); setNotice(''); setBlocked(false); setRetry(false); setSending(false); pending.current = undefined; inFlight.current = false;
    if (tournament) void load();
    return () => { ++sequence.current; reads.current?.abort(); action.current?.abort(); };
  }, [tournament, resultId, version]);
  useEffect(() => {
    const refresh = () => { if (!inFlight.current) void load(true); };
    window.addEventListener('focus', refresh); window.addEventListener('online', refresh);
    const timer = window.setInterval(refresh, 5000);
    return () => { window.clearInterval(timer); window.removeEventListener('focus', refresh); window.removeEventListener('online', refresh); };
  }, [tournament, resultId, version]);
  async function submit() {
    if (inFlight.current || !ready || existing || blocked || !resultId || !exactVersion(version)) return;
    inFlight.current = true; setSending(true); setError(undefined); const generation = sequence.current;
    const operation = pending.current ?? { resultId, expectedResultVersion: version, key: crypto.randomUUID() }; pending.current = operation;
    const abort = new AbortController(); action.current = abort;
    try {
      const response = await session.workspace().client('/api/v1/appeals').command<unknown>('', { resultId: operation.resultId, expectedResultVersion: operation.expectedResultVersion }, { idempotencyKey: operation.key, signal: abort.signal });
      if (generation !== sequence.current || abort.signal.aborted) return;
      const value = readView(response.data, tournament);
      if (response.status !== 201 || value.teamId !== team || value.resultId !== resultId) throw new ApiProtocolError();
      setExisting(value); pending.current = undefined; setRetry(false); setNotice('Апелляция зарегистрирована. Жюри выберет её вручную.'); await load(true);
    } catch (e) {
      if (generation !== sequence.current || abort.signal.aborted) return;
      setError(e); setRetry(true);
      if (e instanceof ApiError && ['WindowClosed', 'BaseNotAppealable'].includes(e.problem?.code ?? '')) setBlocked(true);
      if (e instanceof ApiError && e.problem?.code === 'AlreadyAppealed') await load(true);
    } finally { if (!abort.signal.aborted) { inFlight.current = false; setSending(false); } }
  }
  return <>
    <Link to={`/results?${new URLSearchParams({ tournamentId: tournament })}`}>К результатам команды</Link>
    <button disabled={loading || sending} onClick={() => void load()}>Обновить мои апелляции</button>
    {loading && <p role="status">Загрузка апелляций…</p>}{error !== undefined && <ApiProblemNotice error={error} />}{notice && <p role="status">{notice}</p>}
    {resultId && exactVersion(version) ? <section aria-label="Подача апелляции"><h2>Апелляция по выбранному результату</h2>
      {existing ? <p>Апелляция по этой задаче уже подана: {states[existing.state]}. Новая версия результата не разрешает вторую подачу.</p>
        : <><p>Подача доступна после публикации и до ручного завершения турнира. Допустимость проверит сервер.</p>
          <button disabled={!ready || loading || sending || blocked} onClick={() => void submit()}>{retry ? 'Повторить подачу' : 'Подать апелляцию'}</button></>}
    </section> : <p>Выберите опубликованный результат на странице команды.</p>}
    <section aria-label="Мои апелляции"><h2>Мои апелляции</h2>{ready && items.length === 0 && <p>Апелляций пока нет.</p>}
      <ul>{items.map(item => <li key={item.appealId}>Задача {item.taskId}: {states[item.state]}
        {details.find(d => d.appealId === item.appealId)?.finalPublicationId && <p>Итоговая публикация: {details.find(d => d.appealId === item.appealId)!.finalPublicationId}</p>}
        {item.state === 'AwaitingResultApplication' && <p>Вердикт получен. Ожидаем подтверждения публикации Results.</p>}
        {item.state === 'NeedsResolution' && <p>Результат не применён. Исходное основание сохранено; требуется разбор конфликта.</p>}
      </li>)}</ul>
    </section>
  </>;
}

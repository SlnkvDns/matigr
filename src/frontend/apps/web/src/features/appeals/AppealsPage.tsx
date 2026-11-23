import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ApiError, ApiProtocolError } from '../../shared/api/http';
import { useSession } from '../../shared/auth/SessionContext';
import { tournamentLocation } from '../../shared/tournament';
import { ApiProblemNotice } from '../../shared/ui/ApiProblemNotice';
import { Page } from '../../shared/ui/Page';
import { exactVersion, readList, readView, reviewLabel, scheduleAppealRefresh, taskName } from './appeals';
import type { AppealView } from './appeals';
import type { Standings } from '../results/standings';
import { useStandingsLabels } from '../results/useStandingsLabels';
import { JuryAppeals } from './JuryAppeals';
import './appeals.css';

export function AppealsPage() {
  const { snapshot } = useSession(); const [query] = useSearchParams();
  const tournament = query.get('tournamentId') ?? snapshot.profile?.tournamentIds[0] ?? '';
  const team = snapshot.profile?.roles.includes('Team');
  const jury = snapshot.profile?.roles.some(r => r === 'Jury' || r === 'Senior');
  const names = useStandingsLabels(team || jury ? tournament : '');
  return <Page title="Апелляции" description={jury ? 'Возьмите обращение команды и перепроверьте её решение.' : 'Подайте апелляцию по опубликованному результату и следите за её статусом.'}>
    {team ? <TeamAppeals tournament={tournament} standings={names} />
      : jury ? <JuryAppeals tournament={tournament} standings={names} /> : <p>Для подачи апелляции войдите как команда.</p>}
  </Page>;
}
function TeamAppeals({ tournament, standings }: { tournament: string; standings: Standings | undefined }) {
  const { session, snapshot } = useSession(); const team = snapshot.profile!.teamId!;
  const [query] = useSearchParams(); const resultId = query.get('resultId') ?? ''; const version = query.get('expectedResultVersion') ?? '';
  const [existing, setExisting] = useState<AppealView>();
  const [details, setDetails] = useState<AppealView[]>([]);
  const latest = useRef(new Map<string, AppealView>());
  const [error, setError] = useState<unknown>(); const [loading, setLoading] = useState(false); const [sending, setSending] = useState(false);
  const [ready, setReady] = useState(false); const [retry, setRetry] = useState(false); const [notice, setNotice] = useState(''); const [blocked, setBlocked] = useState(false);
  const sequence = useRef(0); const reads = useRef<AbortController | undefined>(undefined); const action = useRef<AbortController | undefined>(undefined);
  const currentRead = useRef<Promise<void> | undefined>(undefined);
  const pending = useRef<{ resultId: string; expectedResultVersion: string; key: string } | undefined>(undefined); const inFlight = useRef(false);
  function load(preserveError = false) {
    if (reads.current) return currentRead.current;
    currentRead.current = read(preserveError);
    return currentRead.current;
  }
  async function read(preserveError: boolean) {
    if (!tournament) return;
    const generation = sequence.current; const abort = new AbortController(); reads.current = abort;
    setLoading(true); if (!preserveError) setError(undefined);
    try {
      const client = session.workspace().client('/api/v1/appeals');
      const response = await client.get<unknown>(`/me?${new URLSearchParams({ tournamentId: tournament })}`, { signal: abort.signal });
      const list = readList(response.data, tournament, team);
      const details = await Promise.all(list.map(async ({ appealId, version }) => {
        const known = latest.current.get(appealId);
        if (known && BigInt(known.version) >= BigInt(version)) return known;
        const response = await client.get<unknown>(`/${encodeURIComponent(appealId)}`, { signal: abort.signal });
        const view = readView(response.data, tournament);
        if (view.appealId !== appealId || view.teamId !== team) throw new ApiProtocolError();
        return view;
      }));
      if (generation !== sequence.current || abort.signal.aborted) return;
      for (const detail of details) {
        const old = latest.current.get(detail.appealId);
        if (!old || BigInt(detail.version) >= BigInt(old.version)) latest.current.set(detail.appealId, detail);
      }
      const stable = [...latest.current.values()];
      setDetails(stable); setExisting(stable.find(d => d.resultId === resultId)); setReady(true);
    } catch (e) { if (generation === sequence.current && !abort.signal.aborted) setError(e); }
    finally { if (reads.current === abort) { reads.current = undefined; setLoading(false); } }
  }
  useEffect(() => {
    setExisting(undefined); setDetails([]); setReady(false); setLoading(false); latest.current.clear(); setNotice(''); setBlocked(false); setRetry(false); setSending(false); pending.current = undefined; inFlight.current = false;
    if (tournament) void load();
    return () => { ++sequence.current; reads.current?.abort(); reads.current = undefined; action.current?.abort(); };
  }, [tournament, resultId, version]);
  const poll = !!tournament && (!ready || details.some(item => item.state !== 'Closed' && item.state !== 'NeedsResolution'));
  useEffect(() => {
    const refresh = () => { if (!inFlight.current) void load(true); };
    window.addEventListener('focus', refresh); window.addEventListener('online', refresh);
    return () => { window.removeEventListener('focus', refresh); window.removeEventListener('online', refresh); };
  }, [tournament, resultId, version]);
  useEffect(() => {
    if (!poll) return;
    return scheduleAppealRefresh(() => inFlight.current ? undefined : load(true), currentRead.current);
  }, [tournament, resultId, version, poll]);
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
      const known = latest.current.get(value.appealId);
      if (!known || BigInt(value.version) >= BigInt(known.version)) latest.current.set(value.appealId, value);
      setExisting(latest.current.get(value.appealId)); setDetails([...latest.current.values()]);
      pending.current = undefined; setRetry(false); setNotice('Апелляция подана. Она появилась в очереди жюри.'); await load(true);
    } catch (e) {
      if (generation !== sequence.current || abort.signal.aborted) return;
      setError(e); setRetry(true);
      if (e instanceof ApiError && e.problem?.code === 'WindowClosed') setBlocked(true);
      if (e instanceof ApiError && e.problem?.code === 'AlreadyAppealed') {
        setBlocked(true);
        // A read started before the command may still contain an empty list. Refresh after it settles.
        await currentRead.current;
        if (generation === sequence.current) await load(true);
      }
    } finally { if (!abort.signal.aborted) { inFlight.current = false; setSending(false); } }
  }
  return <div className="appeals-workspace">
    {error !== undefined && <ApiProblemNotice error={error} />}
    <section aria-label="Подача апелляции" className="appeals-panel appeals-selection">
      <h2>Подать апелляцию</h2>
      {notice && <p role="status">{notice}</p>}
      {resultId && exactVersion(version)
        ? existing ? <p><strong>{taskName(existing, standings)}.</strong> Апелляция уже подана. По каждой задаче можно обратиться один раз.</p>
          : <p>Команда может один раз обжаловать опубликованный результат по задаче до завершения турнира.</p>
        : <p>Выберите задачу с опубликованным результатом на странице вашей команды.</p>}
      <div className="appeals-actions">
        {resultId && exactVersion(version) && !existing && <button className="appeals-button" disabled={!ready || sending || blocked} onClick={() => void submit()}>{retry ? 'Повторить подачу' : 'Подать апелляцию'}</button>}
        <Link className="appeals-button appeals-secondary" to={tournamentLocation('/results', tournament)}>К результатам команды</Link>
      </div>
    </section>
    <section aria-label="Мои апелляции" className="appeals-panel">
      <div className="appeals-panel-heading">
        <h2>Мои апелляции</h2>
        <button className="appeals-button appeals-secondary" disabled={loading || sending} onClick={() => void load()}>Обновить мои апелляции</button>
      </div>
      {loading && details.length === 0 && <p role="status" className="appeals-empty">Загрузка апелляций…</p>}
      {ready && details.length === 0 && <p className="appeals-empty">Апелляций пока нет.</p>}
      <ul className="appeals-list">{details.map(item => <li key={item.appealId}>
        <div><strong className="appeals-item-name">{taskName(item, standings)}</strong>
          {item.state === 'Closed' && <p>Итог перепроверки опубликован в результатах команды.</p>}
          {item.state === 'NeedsResolution' && <p>Опубликованный результат пока сохраняется. Апелляция требует разбора.</p>}
        </div>
        <span className="appeals-status">{reviewLabel(item)}</span>
      </li>)}</ul>
    </section>
  </div>;
}

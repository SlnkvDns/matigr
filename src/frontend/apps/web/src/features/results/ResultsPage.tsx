import { useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { createHttpClient } from '../../shared/api/http';
import { ApiProblemNotice } from '../../shared/ui/ApiProblemNotice';
import { Page } from '../../shared/ui/Page';
import { readStandings } from './standings';
import type { Standings } from './standings';
import './results.css';

export function ResultsPage() {
  const [query, setQuery] = useSearchParams();
  const tournament = query.get('tournamentId') ?? import.meta.env.VITE_TOURNAMENT_ID ?? '';
  const [draft, setDraft] = useState(tournament);
  const [snapshot, setSnapshot] = useState<Standings>();
  const [error, setError] = useState<unknown>();
  const [loading, setLoading] = useState(false);
  const generation = useRef(0);
  const cancel = useRef<AbortController | undefined>(undefined);
  async function load() {
    if (!tournament) return;
    const revision = ++generation.current; cancel.current?.abort(); const controller = new AbortController(); cancel.current = controller;
    setLoading(true); setError(undefined);
    try {
      const response = await createHttpClient('/api/v1/results').get<unknown>(`/tournaments/${encodeURIComponent(tournament)}/standings`, { signal: controller.signal });
      const view = readStandings(response.data, tournament);
      if (revision === generation.current && !controller.signal.aborted) setSnapshot(view);
    } catch (e) { if (revision === generation.current && !controller.signal.aborted) setError(e); }
    finally { if (revision === generation.current) setLoading(false); }
  }
  useEffect(() => {
    setSnapshot(undefined); setDraft(tournament); void load();
    return () => { ++generation.current; cancel.current?.abort(); };
  }, [tournament]);
  return (
    <Page title="Турнирная таблица" description="Результаты команд и баллы по задачам — в одном месте.">
      <p>Таблица доступна всем, без входа.</p>
      <form className="results-controls" onSubmit={e => { e.preventDefault(); setQuery({ tournamentId: draft.trim() }); }}>
        <label>Идентификатор турнира <input value={draft} onChange={e => setDraft(e.target.value)} /></label>
        <button disabled={!draft.trim()} type="submit">Показать таблицу</button>
      </form>
      {tournament && <button disabled={loading} onClick={() => void load()}>Обновить таблицу</button>}
      {loading && <p role="status">Загрузка результатов…</p>}
      {error !== undefined && <ApiProblemNotice error={error} />}
      {snapshot && <div className="standings-scroll" role="region" aria-label="Результаты команд" tabIndex={0}>
        <table className="standings"><caption>Опубликованные результаты</caption>
          <thead><tr><th scope="col">Место</th><th scope="col">Команда</th>{snapshot.tasks.map(task => <th scope="col" key={task.taskId}>{task.name}</th>)}<th scope="col">Сумма</th></tr></thead>
          <tbody>{snapshot.teams.map(team => <tr key={team.teamId}><td>{team.rank}</td><th scope="row">{team.name}</th>
            {team.cells.map((cell, i) => <td key={snapshot.tasks[i]!.taskId}>{cell.status === 'Pending' ? <span aria-label="Ожидает результата">—</span> : cell.score}</td>)}
            <td>{team.total}</td></tr>)}</tbody>
        </table>
      </div>}
      {snapshot && <p>«—» означает, что результат ещё ожидается. Равные суммы делят место.</p>}
    </Page>
  );
}

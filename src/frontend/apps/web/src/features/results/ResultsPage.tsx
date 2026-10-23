import { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { useSearchParams } from 'react-router';
import { ApiProblemNotice } from '../../shared/ui/ApiProblemNotice';
import { Page } from '../../shared/ui/Page';
import { StandingsController } from './StandingsController';
import './results.css';

export function ResultsPage() {
  const [query, setQuery] = useSearchParams();
  const tournament = query.get('tournamentId') ?? import.meta.env.VITE_TOURNAMENT_ID ?? '';
  const [draft, setDraft] = useState(tournament);
  const controller = useMemo(() => new StandingsController(tournament), [tournament]);
  const { snapshot, error, loading, connection } = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  useEffect(() => {
    setDraft(tournament); controller.start(); return () => controller.stop();
  }, [tournament, controller]);
  return (
    <Page title="Турнирная таблица" description="Результаты команд и баллы по задачам — в одном месте.">
      <p>Таблица доступна всем, без входа.</p>
      <form className="results-controls" onSubmit={e => { e.preventDefault(); setQuery({ tournamentId: draft.trim() }); }}>
        <label>Идентификатор турнира <input value={draft} onChange={e => setDraft(e.target.value)} /></label>
        <button disabled={!draft.trim()} type="submit">Показать таблицу</button>
      </form>
      {tournament && <button disabled={loading} onClick={() => void controller.refresh()}>Обновить таблицу</button>}
      {tournament && connection === 'degraded' && <p role="status">Связь нестабильна. Проверяем обновления; доступна последняя полученная версия.</p>}
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

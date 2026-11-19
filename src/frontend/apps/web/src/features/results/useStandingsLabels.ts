import { useEffect, useState } from 'react';
import { createHttpClient } from '../../shared/api/http';
import { readStandings } from './standings';
import type { Standings } from './standings';

export function useStandingsLabels(tournament: string) {
  const [standings, setStandings] = useState<Standings>();
  useEffect(() => {
    if (!tournament) return;
    const request = new AbortController();
    void createHttpClient('/api/v1/results').get<unknown>(`/tournaments/${encodeURIComponent(tournament)}/standings`, { signal: request.signal })
      .then(({ data }) => { if (!request.signal.aborted) setStandings(readStandings(data, tournament)); })
      .catch(() => { /* Display names are optional. Review actions use the owning API. */ });
    return () => request.abort();
  }, [tournament]);
  return standings?.tournamentId === tournament ? standings : undefined;
}

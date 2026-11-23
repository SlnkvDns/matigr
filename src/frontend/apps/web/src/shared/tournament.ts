export const demoTournamentId = '0344e06b-4ea1-582e-bd91-e832719773f2';

export function tournamentLocation(pathname: string, tournamentId: string | null) {
  return {
    pathname,
    search: tournamentId ? `?${new URLSearchParams({ tournamentId })}` : '',
  };
}

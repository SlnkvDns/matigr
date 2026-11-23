import { Link, Navigate, NavLink, Outlet, Route, Routes, useSearchParams } from 'react-router';
import { CheckingPage } from '../features/checking/CheckingPage';
import { ResultsPage } from '../features/results/ResultsPage';
import { AppealsPage } from '../features/appeals/AppealsPage';
import { Page } from '../shared/ui/Page';
import { PrivateSessionBoundary } from '../shared/auth/SessionContext';
import { SessionPanel } from '../shared/auth/SessionPanel';
import { tournamentLocation } from '../shared/tournament';

function Layout() {
  const [query] = useSearchParams();
  const tournamentId = query.get('tournamentId');
  return (
    <div className="app-shell">
      <a className="skip-link" href="#main">К содержимому</a>
      <header className="app-header">
        <Link className="brand" to={tournamentLocation('/results', tournamentId)} aria-label="Матигр — к таблице">
          <span className="brand-mark" aria-hidden="true">м</span>
          <span>матигр<span className="brand-caption">математические турниры</span></span>
        </Link>
        <nav className="main-nav" aria-label="Основная навигация">
          <NavLink to={tournamentLocation('/results', tournamentId)}>Таблица</NavLink>
          <NavLink to={tournamentLocation('/checking', tournamentId)}>Проверка</NavLink>
          <NavLink to={tournamentLocation('/appeals', tournamentId)}>Апелляции</NavLink>
        </nav>
      </header>
      <SessionPanel />
      <main id="main"><Outlet /></main>
      <footer className="app-footer"><span>Матигр</span><span>Математика объединяет</span></footer>
    </div>
  );
}

function ResultsRedirect() {
  const [query] = useSearchParams();
  return <Navigate to={tournamentLocation('/results', query.get('tournamentId'))} replace />;
}

function NotFound() {
  const [query] = useSearchParams();
  return (
    <Page title="Страница не найдена" description="Проверьте адрес или вернитесь к турнирной таблице.">
      <Link className="text-link" to={tournamentLocation('/results', query.get('tournamentId'))}>К таблице <span aria-hidden="true">↗</span></Link>
    </Page>
  );
}

export function AppRoutes() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<ResultsRedirect />} />
        <Route path="results" element={<ResultsPage />} />
        <Route path="checking" element={<PrivateSessionBoundary><CheckingPage /></PrivateSessionBoundary>} />
        <Route path="appeals" element={<PrivateSessionBoundary><AppealsPage /></PrivateSessionBoundary>} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}

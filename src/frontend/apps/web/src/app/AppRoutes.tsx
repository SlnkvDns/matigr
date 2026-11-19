import { Link, Navigate, NavLink, Outlet, Route, Routes } from 'react-router';
import { CheckingPage } from '../features/checking/CheckingPage';
import { ResultsPage } from '../features/results/ResultsPage';
import { AppealsPage } from '../features/appeals/AppealsPage';
import { Page } from '../shared/ui/Page';
import { PrivateSessionBoundary } from '../shared/auth/SessionContext';
import { SessionPanel } from '../shared/auth/SessionPanel';

function Layout() {
  return (
    <div className="app-shell">
      <a className="skip-link" href="#main">К содержимому</a>
      <header className="app-header">
        <Link className="brand" to="/results" aria-label="Матигр — к таблице">
          <span className="brand-mark" aria-hidden="true">м</span>
          <span>матигр<span className="brand-caption">математические турниры</span></span>
        </Link>
        <nav className="main-nav" aria-label="Основная навигация">
          <NavLink to="/results">Таблица</NavLink>
          <NavLink to="/checking">Проверка</NavLink>
          <NavLink to="/appeals">Апелляции</NavLink>
        </nav>
      </header>
      <SessionPanel />
      <main id="main"><Outlet /></main>
      <footer className="app-footer"><span>Матигр</span><span>Математика объединяет</span></footer>
    </div>
  );
}

export function AppRoutes() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<Navigate to="/results" replace />} />
        <Route path="results" element={<ResultsPage />} />
        <Route path="checking" element={<PrivateSessionBoundary><CheckingPage /></PrivateSessionBoundary>} />
        <Route path="appeals" element={<PrivateSessionBoundary><AppealsPage /></PrivateSessionBoundary>} />
        <Route path="*" element={
          <Page title="Страница не найдена" description="Проверьте адрес или вернитесь к турнирной таблице.">
            <Link className="text-link" to="/results">К таблице <span aria-hidden="true">↗</span></Link>
          </Page>
        } />
      </Route>
    </Routes>
  );
}

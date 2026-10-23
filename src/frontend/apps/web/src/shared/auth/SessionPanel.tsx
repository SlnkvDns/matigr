import { useState } from 'react';
import { ApiProblemNotice } from '../ui/ApiProblemNotice';
import { useSession } from './SessionContext';
import type { UserRole } from './identity';

const roleLabels: Record<UserRole, string> = { Jury: 'Член жюри', Senior: 'Старший жюри', Team: 'Команда', Organizer: 'Организатор' };

export function SessionPanel() {
  const { session, snapshot } = useSession();
  const [selectedKey, setSelectedKey] = useState('');
  return (
    <section className="session-panel" aria-label="Профиль пользователя">
      <div className="session-summary" role="status" aria-live="polite">
        {snapshot.profile ? <>
          <span className="session-name">{snapshot.profile.displayName}</span>
          <span className="session-description">{snapshot.profile.roles.map(role => roleLabels[role]).join(', ')}</span>
        </> : <>
          <span className="session-name">{snapshot.status === 'selecting' ? 'Выполняется вход…' : 'Гость'}</span>
          <span className="session-description">Таблица доступна без входа</span>
        </>}
      </div>
      {session.provider && <form className="session-selector" aria-label="Тестовый вход" onSubmit={event => {
        event.preventDefault();
        if (selectedKey) void session.select(selectedKey);
      }}>
        <label htmlFor="test-subject">Тестовый пользователь</label>
        <div className="session-controls">
          <select id="test-subject" value={selectedKey} onChange={event => setSelectedKey(event.target.value)}>
            <option value="">Выберите пользователя</option>
            {session.provider.choices.map(choice => <option key={choice.key} value={choice.key}>{choice.label}</option>)}
          </select>
          <button type="submit" disabled={!selectedKey}>Выбрать</button>
          {snapshot.status !== 'anonymous' && <button className="session-exit" type="button" onClick={() => session.signOut()}>Выйти</button>}
        </div>
      </form>}
      {snapshot.notice && <p className="session-notice" role="status">
        {snapshot.notice === 'expired' ? 'Сессия истекла. Выберите пользователя снова.' : 'Вход больше не действует. Выберите пользователя снова.'}
      </p>}
      {snapshot.error !== undefined && <ApiProblemNotice error={snapshot.error} />}
    </section>
  );
}

import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ApiError, ApiNetworkError } from '../api/http';
import { ApiProblemNotice } from './ApiProblemNotice';

describe('request error presentation', () => {
  it('shows a useful message and correlation reference without raw server title/detail', () => {
    const error = new ApiError(409, { type: 'about:blank', title: 'internal-sensitive-title', status: 409,
      code: 'ReviewerBusy', correlationId: 'request-42', detail: 'private-material' });
    render(<ApiProblemNotice error={error} />);
    expect(screen.getByRole('alert', { name: 'Ошибка запроса' })).toBeInTheDocument();
    expect(screen.getByText('У вас уже есть незавершённая проверка.')).toBeInTheDocument();
    expect(screen.getByText('request-42')).toBeInTheDocument();
    expect(screen.queryByText(/internal-sensitive-title|private-material/)).not.toBeInTheDocument();
  });

  it.each([
    [401, 'Для этого действия нужен вход.'], [403, 'У вас нет доступа к этому действию.'],
    [503, 'Сервис временно недоступен. Попробуйте позже.'],
  ])('presents HTTP %s without inventing a successful outcome', (status, message) => {
    render(<ApiProblemNotice error={new ApiError(status)} />);
    expect(screen.getByText(message)).toBeInTheDocument();
  });

  it('keeps a lost-response outcome uncertain and does not retry an operation', () => {
    render(<ApiProblemNotice error={new ApiNetworkError()} />);
    expect(screen.getByText(/Результат отправленного действия пока может быть неизвестен/)).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('does not print arbitrary exception messages to the page', () => {
    render(<ApiProblemNotice error={new Error('secret-from-response')} />);
    expect(screen.queryByText(/secret-from-response/)).not.toBeInTheDocument();
    expect(screen.getByText('Не удалось выполнить запрос. Попробуйте позже.')).toBeInTheDocument();
  });
});

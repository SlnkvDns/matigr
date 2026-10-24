import { ApiError, ApiNetworkError } from '../api/http';

function message(error: unknown): string {
  if (error instanceof ApiNetworkError) return 'Не удалось связаться с сервером. Результат отправленного действия пока может быть неизвестен.';
  if (error instanceof ApiError) {
    switch (error.problem?.code) {
      case 'ReviewerBusy': return 'У вас уже есть незавершённая проверка.';
      case 'CheckVersionConflict': return 'Состояние проверки изменилось. Обновите данные.';
      case 'IdempotencyConflict': return 'Этот запрос уже использован для другого действия.';
      case 'AlreadyAppealed': return 'Апелляция по этой задаче уже подана.';
      case 'WindowClosed': return 'Турнир завершён. Новые апелляции больше не принимаются.';
      case 'BaseNotAppealable': return 'Итог старшего после передачи является окончательным.';
      case 'ResultVersionConflict': return 'Опубликованный результат изменился. Откройте его заново.';
      case 'AppealVersionConflict': return 'Состояние апелляции изменилось. Обновите данные.';
    }
    if (error.status === 401) return 'Для этого действия нужен вход.';
    if (error.status === 403) return 'У вас нет доступа к этому действию.';
    if (error.status === 503) return 'Сервис временно недоступен. Попробуйте позже.';
  }
  return 'Не удалось выполнить запрос. Попробуйте позже.';
}

export function ApiProblemNotice({ error }: { error: unknown }) {
  const correlationId = error instanceof ApiError ? error.problem?.correlationId : undefined;
  return (
    <section className="api-problem" role="alert" aria-label="Ошибка запроса">
      <h2>Не удалось выполнить запрос</h2>
      <p>{message(error)}</p>
      {correlationId && <p className="api-problem-reference">Код обращения: <span>{correlationId}</span></p>}
    </section>
  );
}

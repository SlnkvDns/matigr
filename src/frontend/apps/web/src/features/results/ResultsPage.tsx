import { Page } from '../../shared/ui/Page';
import { SectionUnavailable } from '../../shared/ui/SectionUnavailable';

export function ResultsPage() {
  return (
    <Page title="Турнирная таблица" description="Результаты команд и баллы по задачам — в одном месте.">
      <SectionUnavailable description="Здесь появятся опубликованные результаты турнира. Таблица будет доступна всем, без входа." />
    </Page>
  );
}

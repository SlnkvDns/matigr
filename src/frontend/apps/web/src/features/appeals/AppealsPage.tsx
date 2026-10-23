import { Page } from '../../shared/ui/Page';
import { SectionUnavailable } from '../../shared/ui/SectionUnavailable';

export function AppealsPage() {
  return (
    <Page title="Апелляции" description="Обращения команд по опубликованным результатам.">
      <SectionUnavailable description="Здесь команды смогут подать апелляцию и следить за её рассмотрением, а жюри — выбрать обращение для перепроверки." />
    </Page>
  );
}

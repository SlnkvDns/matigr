import { Page } from '../../shared/ui/Page';
import { SectionUnavailable } from '../../shared/ui/SectionUnavailable';

export function CheckingPage() {
  return (
    <Page title="Проверка решений" description="Рабочее место жюри для проверки решений команд.">
      <SectionUnavailable description="Здесь появятся очередь решений и ваша текущая проверка." />
    </Page>
  );
}

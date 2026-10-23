interface SectionUnavailableProps {
  description: string;
}

export function SectionUnavailable({ description }: SectionUnavailableProps) {
  return (
    <section className="unavailable-panel" aria-labelledby="section-unavailable-title">
      <div className="unavailable-symbol" aria-hidden="true"><span /><span /><span /></div>
      <div className="unavailable-copy">
        <p className="status-label"><span aria-hidden="true" />Раздел готовится</p>
        <h2 id="section-unavailable-title">Скоро здесь</h2>
        <p>{description}</p>
      </div>
    </section>
  );
}

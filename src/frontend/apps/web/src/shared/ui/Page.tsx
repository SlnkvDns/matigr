import { useEffect, useRef } from 'react';
import type { ReactNode } from 'react';

interface PageProps {
  title: string;
  description: string;
  children: ReactNode;
}

export function Page({ title, description, children }: PageProps) {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    document.title = `${title} · Матигр`;
    heading.current?.focus({ preventScroll: true });
  }, [title]);
  return (
    <div className="page">
      <div className="page-heading">
        <p className="eyebrow">Математический турнир</p>
        <h1 ref={heading} tabIndex={-1}>{title}</h1>
        <p className="page-description">{description}</p>
      </div>
      {children}
    </div>
  );
}

import type { ReactNode } from 'react';

export function Screen({
  title,
  lede,
  actions,
  children,
}: {
  title: string;
  lede?: string;
  actions?: ReactNode;
  children: ReactNode;
}) {
  return (
    <>
      <header className="screen-head">
        <div>
          <h1>{title}</h1>
          {lede ? <p>{lede}</p> : null}
        </div>
        {actions}
      </header>
      {children}
    </>
  );
}

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

/* An honest placeholder. It says which step builds the screen and which
 * endpoints it will call, because a screen full of grey rectangles pretending
 * to be content tells nobody anything — and these are the notes the next
 * session actually wants. */
export function Planned({ step, endpoints }: { step: string; endpoints: string[] }) {
  return (
    <div className="state">
      <h2>Not built yet</h2>
      <p>
        This screen is {step}. The design is approved on the canvas; the markup
        lands with the step. It will call:
      </p>
      <ul>
        {endpoints.map((e) => (
          <li key={e}>{e}</li>
        ))}
      </ul>
    </div>
  );
}

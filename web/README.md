# JobKeep — front end

React + Vite + TypeScript. Talks to the ASP.NET Core API in `../src`.

```bash
npm install
npm run dev     # http://localhost:5173
```

The API must be running on `http://localhost:5080`:

```bash
docker start zen_agnesi        # dev Postgres
cd ../src && dotnet run
```

## Why there is no dev-server proxy

The API has a real, named CORS policy allowing `http://localhost:5173`
(`src/appsettings.Development.json`). A Vite proxy would make requests
same-origin in development and the first deploy would be the first time CORS was
ever exercised. The port is pinned with `strictPort` for the same reason — a
silent move to 5174 fails every preflight and reads like a React bug.

Point it elsewhere with `VITE_API_BASE_URL`; see `.env.example`.

## Structure

```
src/routes/      one file per screen; a screen owns its data fetching
src/components/  shared only once a SECOND screen needs it
src/lib/api.ts   the fetch core, ApiError, shared domain types
src/styles/      tokens.css (the palette lives here and nowhere else), base, shell
```

Two rules worth knowing before editing:

- **A raw hex outside `tokens.css` is a bug.** The artboards carried 145 unnamed
  hex values; naming them was the whole of step 6.2.
- **On a tinted surface the label is the `-dark` token, never the base.** That is
  what holds WCAG 2.2 AA without auditing components one at a time.

The full rules are in `docs/phases/phase-7-feature-expansion.md`.

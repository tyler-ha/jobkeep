# Job Application Tracker

A personal job-application tracker with AI-powered job description
analysis and ATS compatibility checking. Built as a portfolio project:
C# / ASP.NET Core backend, deployed serverless on AWS, AI features via
Ollama (local) or a hosted API (deployed).

## Why this project

Built while job hunting in the Melbourne market — solves a real problem
(tracking applications, understanding fit against job descriptions)
while building demonstrable C# + AWS + AI integration experience.

## Status

| Phase | What | Status |
|---|---|---|
| 1 | Local API, in-memory storage | Done — see `src/` |
| 2 | DynamoDB storage (local, then cloud) | Not started |
| 3 | Deploy to AWS Lambda + API Gateway | Not started |
| 4 | AI job-description analyzer | Not started |
| 5 | ATS compatibility check | Not started |
| 6 | Front end | Not started |

Full detail for each phase, including cost notes and interview talking
points, is in `docs/`.

## Quick start (Phase 1, current state)

```bash
cd src
dotnet restore
dotnet run
```

Runs on `http://localhost:5080`. Example requests:

```bash
curl -X POST http://localhost:5080/applications \
  -H "Content-Type: application/json" \
  -d '{"company":"Amazon","role":"SDE I","notes":"Applied via referral"}'

curl http://localhost:5080/applications
```

## Project structure

```
JobTracker/
├── CLAUDE.md              # Context file for Claude Code
├── README.md              # This file
├── docs/                  # One doc per build phase
│   ├── phase-1-local-api.md
│   ├── phase-2-dynamodb.md
│   ├── phase-3-aws-deploy.md
│   ├── phase-4-ai-analyzer.md
│   ├── phase-5-ats-check.md
│   └── phase-6-frontend.md
└── src/                   # The actual .NET project
    ├── JobTracker.csproj
    ├── Program.cs
    ├── Models/
    ├── Repositories/
    └── Properties/
```

## STAR log

Keep a running log (separate from this repo — a spreadsheet or notes
app is fine) of specific decisions and moments from building this, with
a number attached wherever possible. This is raw material for behavioral
interviews later — capture it close to when it happens, not months
after. See phase docs for "interview talking points" sections as
starting prompts for entries.

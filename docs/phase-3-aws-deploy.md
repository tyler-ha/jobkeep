# Phase 3 — Deploy to AWS Lambda + API Gateway

**Status: Not started**

## Goal

Get a real, live URL — the actual "shipped to the cloud" milestone for
the portfolio.

## Plan

1. Add `Amazon.Lambda.AspNetCoreServer` package to adapt the existing
   ASP.NET Core minimal API to run inside Lambda with no rewrite.
2. Add a Lambda entry point (`LambdaEntryPoint.cs`) wrapping the existing
   `Program.cs` — the endpoint code from Phase 1/2 does not change.
3. Deploy using the AWS `dotnet lambda` CLI tool or AWS SAM:
   ```bash
   dotnet tool install -g Amazon.Lambda.Tools
   dotnet lambda deploy-serverless
   ```
4. Wire up API Gateway in front of the Lambda function (the deploy tool
   does this automatically with a default template).
5. Point the DynamoDB repository from Phase 2 at real AWS DynamoDB
   instead of the local Docker instance.
6. Add basic auth (API key via API Gateway, or Cognito) so the endpoint
   isn't wide open on the public internet.
7. Set API Gateway throttling limits and a billing alert as guardrails.

## Cost notes

- Lambda + API Gateway free tier: 1M requests/month free (Lambda's
  compute free tier is permanent, not just first-year).
- At personal-project usage, this phase should cost close to $0/month.
- The main new cost risk here is leaving something *always-on* by
  mistake (e.g. a provisioned-capacity DynamoDB table) — stick to
  on-demand/serverless everywhere.

## Interview talking points from this phase

- Serverless deployment tradeoffs vs. a traditional always-on server.
- API Gateway throttling and auth as basic production-readiness habits.
- Actually watching a billing dashboard and reasoning about cost —
  a real skill, not just theory.

## Next

Phase 4 — add the AI job-description analyzer.

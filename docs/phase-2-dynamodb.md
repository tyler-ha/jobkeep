# Phase 2 — Swap storage to DynamoDB (local first)

**Status: Not started**

## Goal

Replace in-memory storage with DynamoDB, but develop against a free local
copy before touching real AWS — so this phase costs nothing either.

## Plan

1. Run **DynamoDB Local** via Docker:
   ```bash
   docker run -p 8000:8000 amazon/dynamodb-local
   ```
2. Add the AWS SDK for .NET to the project:
   ```bash
   dotnet add package AWSSDK.DynamoDBv2
   ```
3. Create a table named `Applications` with:
   - Partition key: `UserId` (string) — hardcode one value for now, since
     this is a single-user personal project.
   - Sort key: `Id` (string) — the application's GUID.
4. Implement `Repositories/DynamoDbJobApplicationRepository.cs`,
   implementing the same `IJobApplicationRepository` interface from
   Phase 1. No changes needed to `Program.cs` endpoint logic.
5. Point the SDK client at `http://localhost:8000` for local dev
   (a config flag), and at real AWS endpoints for the deployed version
   in Phase 3.
6. Swap the one registration line in `Program.cs`:
   ```csharp
   builder.Services.AddSingleton<IJobApplicationRepository, DynamoDbJobApplicationRepository>();
   ```

## Cost notes

- DynamoDB Local: completely free, runs on your machine, no AWS account
  charges at all.
- Real DynamoDB (once deployed in Phase 3): on-demand pricing, effectively
  free at personal-project volume — pennies a month at most.

## Interview talking points from this phase

- NoSQL data modeling: why partition key / sort key design matters for
  DynamoDB access patterns, unlike relational tables.
- Testing against a local double of a cloud service before paying for
  the real thing — a genuinely good engineering habit.

## Next

Phase 3 — deploy the API to AWS Lambda + API Gateway.

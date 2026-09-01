# The API, as a container. Used by compose.yaml at the repo root, which is the only
# way this app is launched since run.cmd was deleted on 2026-09-01.
#
# Multi-stage, because the SDK is ~800MB of compiler and the app needs none of it
# at run time -- the shipped image is the ~220MB ASP.NET runtime plus the publish
# output.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The project files on their own first. Docker caches layers, so restoring before
# the source is copied means an ordinary code edit re-uses the NuGet download
# instead of repeating it -- the difference between a 10-second rebuild and a
# 2-minute one.
#
# Phase 13.1 turned one csproj into nine; 13.2 added Skills and made it ten. They are copied with their directory
# structure intact (COPY <dir>/<file> <dir>/) because a ProjectReference is a
# relative path: flattening them here would restore fine and then fail to build.
# Directory.Build.props supplies the TFM to all of them, so it has to come too.
COPY Directory.Build.props .
COPY Jobkeep.SharedKernel/Jobkeep.SharedKernel.csproj                   Jobkeep.SharedKernel/
COPY Jobkeep.Contracts/Jobkeep.Contracts.csproj                         Jobkeep.Contracts/
COPY Jobkeep.Infrastructure.Data/Jobkeep.Infrastructure.Data.csproj     Jobkeep.Infrastructure.Data/
COPY Jobkeep.Modules.Applications/Jobkeep.Modules.Applications.csproj   Jobkeep.Modules.Applications/
COPY Jobkeep.Modules.Analytics/Jobkeep.Modules.Analytics.csproj         Jobkeep.Modules.Analytics/
COPY Jobkeep.Modules.Ai/Jobkeep.Modules.Ai.csproj                       Jobkeep.Modules.Ai/
COPY Jobkeep.Modules.Ats/Jobkeep.Modules.Ats.csproj                     Jobkeep.Modules.Ats/
COPY Jobkeep.Modules.Documents/Jobkeep.Modules.Documents.csproj         Jobkeep.Modules.Documents/
COPY Jobkeep.Modules.Skills/Jobkeep.Modules.Skills.csproj               Jobkeep.Modules.Skills/
COPY Jobkeep.Api/Jobkeep.Api.csproj                                     Jobkeep.Api/
RUN dotnet restore Jobkeep.Api/Jobkeep.Api.csproj

COPY . .

# Jobkeep.Api.csproj, not Jobkeep.slnx: since Phase 2.6 the solution includes the
# test project, which lives in ../tests -- outside this build context, so a
# solution-level restore would fail on a path that does not exist here. Publishing
# the Api project pulls the other nine in through its ProjectReferences.
RUN dotnet publish Jobkeep.Api/Jobkeep.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Run as the non-root user the base image already defines (APP_UID). Nothing in
# src/ writes to the filesystem -- uploads are parsed in memory and land in
# Postgres -- so there is no directory to chown, which is the usual reason this
# line gets dropped.
USER $APP_UID

# 8080, not the 5080 that launchSettings.json names. Since .NET 8 these images
# default to ASPNETCORE_HTTP_PORTS=8080 precisely so a non-root process can bind
# it (binding 80 needs a privilege this user does not have). compose maps
# 5080 -> 8080 so every doc, every appsettings file and every bookmark still
# says 5080.
EXPOSE 8080

ENTRYPOINT ["dotnet", "Jobkeep.Api.dll"]

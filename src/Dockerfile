# The API, as a container. Used by compose.yaml at the repo root; `run.cmd` does
# not need it (that path runs `dotnet run` natively).
#
# Multi-stage, because the SDK is ~800MB of compiler and the app needs none of it
# at run time -- the shipped image is the ~220MB ASP.NET runtime plus the publish
# output.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The project file on its own first. Docker caches layers, so restoring before
# the source is copied means an ordinary code edit re-uses the NuGet download
# instead of repeating it -- the difference between a 10-second rebuild and a
# 2-minute one.
COPY Jobkeep.csproj .
RUN dotnet restore Jobkeep.csproj

COPY . .

# Jobkeep.csproj, not Jobkeep.slnx: since Phase 2.6 the solution includes the
# test project, which lives in ../tests -- outside this build context, so a
# solution-level restore would fail on a path that does not exist here.
RUN dotnet publish Jobkeep.csproj -c Release -o /app --no-restore

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

ENTRYPOINT ["dotnet", "Jobkeep.dll"]

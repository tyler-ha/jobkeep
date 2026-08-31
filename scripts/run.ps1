<#
.SYNOPSIS
    Start JobKeep end to end: Postgres, the API, the front end.

.DESCRIPTION
    Brings up the whole local stack in dependency order and waits for each
    layer to actually answer before starting the next one, so a failure is
    reported against the thing that failed rather than as a fetch error three
    layers up.

        1. Docker Desktop         (launched if it isn't running)
        2. Postgres 16            (an existing container is reused; the data
                                   volume, and so the seeded records, survive)
        3. The API on :5080       (dotnet run; migrations auto-apply in
                                   Development)
        4. The front end on :5173

    Ctrl+C stops everything it started. If that ever fails to clean up -- or if
    a previous run was killed from outside -- `run.ps1 -Stop` tears the stack
    down on its own.

    Why this exists: a stale `dotnet run` holding Jobkeep.exe makes the next
    build fail with MSB3027 ("Exceeded retry count of 10 ... locked by
    Jobkeep"), which reads like a build problem and isn't one. Step 3 kills any
    leftover before it builds, so that error cannot happen twice for the same
    reason.

.PARAMETER Stop
    Tear down a running stack and exit. Leaves the Postgres container running,
    since it holds the seeded data and costs nothing idle -- pass -StopDatabase
    to stop that too.

.PARAMETER StopDatabase
    With -Stop, also stop the Postgres container.

.PARAMETER NoFrontend
    Bring up Postgres and the API only. For backend work, or for running the
    test suite against a live database.

.PARAMETER NoBrowser
    Don't open the browser when the front end is ready.

.PARAMETER TimeoutSeconds
    How long to wait for any single layer. The API's first build after a clean
    is the slow one; everything else answers in seconds.

.EXAMPLE
    .\run.cmd
    Start everything, open the browser, stream status. Ctrl+C stops it all.

.EXAMPLE
    .\run.cmd -NoFrontend
    Postgres + API only, for backend work.

.EXAMPLE
    .\run.cmd -Stop
    Kill a stack left running by a crashed launcher.
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$StopDatabase,
    [switch]$NoFrontend,
    [switch]$NoBrowser,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$Root         = Split-Path -Parent $PSScriptRoot
$ApiProject   = Join-Path $Root 'src\Jobkeep.csproj'
$WebDir       = Join-Path $Root 'web'
$LogDir       = Join-Path $Root 'logs'
$PidFile      = Join-Path $LogDir 'run-pids.json'

$ApiPort      = 5080
$WebPort      = 5173
$DbPort       = 5432

$ApiUrl       = "http://localhost:$ApiPort"
$WebUrl       = "http://localhost:$WebPort"

# Must match src/appsettings.Development.json's connection string. Changing the
# image here without changing that is how you get a database the app can't see.
$DbImage      = 'postgres:16-alpine'
$DbContainer  = 'jobkeep-db'
$DbPassword   = 'dev'
$DbName       = 'jobkeep'

# ---------------------------------------------------------------------------
# Output helpers. Plain ASCII markers -- this runs in cmd.exe as often as in a
# terminal that can render anything fancier.
# ---------------------------------------------------------------------------

function Write-Step  { param([string]$m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    { param([string]$m) Write-Host "    ok   $m" -ForegroundColor Green }
function Write-Note  { param([string]$m) Write-Host "    ..   $m" -ForegroundColor DarkGray }
function Write-Warn2 { param([string]$m) Write-Host "    warn $m" -ForegroundColor Yellow }
function Write-Fail  { param([string]$m) Write-Host "    FAIL $m" -ForegroundColor Red }

# ---------------------------------------------------------------------------
# Process / port helpers
# ---------------------------------------------------------------------------

# Dial the *name*, not 127.0.0.1. Vite binds ::1 only, so an IPv4-literal probe
# reports a healthy dev server as down and the script waits out its whole
# timeout on a front end that came up in 250ms. The string overload tries every
# address the name resolves to.
function Test-PortOpen {
    param([int]$Port, [string]$TargetHost = 'localhost')
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.BeginConnect($TargetHost, $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(400)) { return $false }
        $client.EndConnect($connect)
        return $true
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Get-PortOwner {
    param([int]$Port)
    try {
        Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop |
            Select-Object -ExpandProperty OwningProcess -Unique
    } catch {
        @()
    }
}

# taskkill /T kills the whole tree, which is the point: `dotnet run` launches
# Jobkeep.exe as a child, and killing only the parent is exactly how the
# orphaned process that causes MSB3027 gets created.
function Stop-Tree {
    param([int]$ProcessId, [string]$Label)
    if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return }
    & taskkill.exe /PID $ProcessId /T /F 2>&1 | Out-Null
    Write-Note "stopped $Label (pid $ProcessId)"
}

function Wait-For {
    param(
        [string]$What,
        [scriptblock]$Until,
        [int]$Seconds = 60,
        [scriptblock]$OnFail
    )
    $deadline = (Get-Date).AddSeconds($Seconds)
    $spin = 0
    while ((Get-Date) -lt $deadline) {
        if (& $Until) { return $true }
        Start-Sleep -Milliseconds 700
        $spin++
        if ($spin % 10 -eq 0) {
            $left = [int]($deadline - (Get-Date)).TotalSeconds
            Write-Note "still waiting for $What (${left}s left)"
        }
    }
    Write-Fail "$What did not come up within ${Seconds}s"
    if ($OnFail) { & $OnFail }
    return $false
}

# ---------------------------------------------------------------------------
# Teardown
# ---------------------------------------------------------------------------

function Stop-Stack {
    param([switch]$IncludeDatabase)

    Write-Step 'Stopping'

    # Launcher PIDs recorded by a previous run, if the file survived.
    if (Test-Path $PidFile) {
        try {
            $saved = Get-Content $PidFile -Raw | ConvertFrom-Json
            foreach ($entry in $saved.PSObject.Properties) {
                Stop-Tree -ProcessId ([int]$entry.Value) -Label $entry.Name
            }
        } catch {
            Write-Warn2 "could not read $PidFile ($($_.Exception.Message))"
        }
        Remove-Item $PidFile -ErrorAction SilentlyContinue
    }

    # Whatever is actually holding the ports, whoever started it. This is the
    # part that works after a crashed launcher.
    foreach ($p in @(@{Port=$ApiPort; Name='api'}, @{Port=$WebPort; Name='front end'})) {
        foreach ($owner in Get-PortOwner -Port $p.Port) {
            Stop-Tree -ProcessId ([int]$owner) -Label "$($p.Name) on :$($p.Port)"
        }
    }

    # The specific orphan that breaks the next build.
    Get-Process -Name Jobkeep -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Tree -ProcessId $_.Id -Label 'Jobkeep.exe'
    }

    if ($IncludeDatabase) {
        $name = Get-DbContainer
        if ($name) {
            & docker stop $name 2>&1 | Out-Null
            Write-Note "stopped container $name"
        }
    }

    Write-Ok 'stopped'
}

# ---------------------------------------------------------------------------
# 1. Docker
# ---------------------------------------------------------------------------

function Test-DockerUp {
    & docker info 2>&1 | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Start-DockerDesktop {
    $candidates = @(
        "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe",
        "${env:ProgramFiles(x86)}\Docker\Docker\Docker Desktop.exe"
    )
    $exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $exe) { return $false }
    Write-Note 'launching Docker Desktop'
    Start-Process -FilePath $exe | Out-Null
    return $true
}

# ---------------------------------------------------------------------------
# 2. Postgres
# ---------------------------------------------------------------------------

# Any container built from the expected image, running or not. The dev database
# was originally created by a bare `docker run`, so it carries a random name
# like `zen_agnesi` -- reusing it by image keeps the seeded data rather than
# quietly starting a second, empty one alongside.
function Get-DbContainer {
    $found = & docker ps -a --filter "ancestor=$DbImage" --format '{{.Names}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $found) { return $null }
    $names = @($found -split "`n" | Where-Object { $_.Trim() })
    if ($names -contains $DbContainer) { return $DbContainer }
    return $names[0].Trim()
}

function Start-Database {
    if (Test-PortOpen -Port $DbPort) {
        Write-Ok "postgres already listening on :$DbPort"
        return $true
    }

    $name = Get-DbContainer
    if ($name) {
        Write-Note "starting existing container $name"
        & docker start $name 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Fail "docker start $name failed"; return $false }
    } else {
        Write-Note "no $DbImage container found -- creating $DbContainer"
        & docker run -d --name $DbContainer -p "${DbPort}:5432" `
            -e "POSTGRES_PASSWORD=$DbPassword" -e "POSTGRES_DB=$DbName" $DbImage 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Fail 'docker run failed'; return $false }
        $name = $DbContainer
        Write-Warn2 'fresh database -- it has no applications, resumes or imports in it yet'
    }

    # pg_isready, not just an open port: Postgres accepts TCP before it will
    # accept queries, and the API's startup migration is the first query.
    return Wait-For -What "postgres ($name)" -Seconds 60 -Until {
        & docker exec $name pg_isready -U postgres -d $DbName 2>&1 | Out-Null
        $LASTEXITCODE -eq 0
    }
}

# ---------------------------------------------------------------------------
# 3. The API
# ---------------------------------------------------------------------------

function Start-Api {
    # The MSB3027 guard. A leftover Jobkeep.exe holds bin\Debug\net10.0\Jobkeep.exe
    # open, and the build fails to copy over it after ten retries.
    $stale = Get-Process -Name Jobkeep -ErrorAction SilentlyContinue
    if ($stale) {
        Write-Warn2 "found $($stale.Count) leftover Jobkeep process(es) holding the build output -- killing"
        $stale | ForEach-Object { Stop-Tree -ProcessId $_.Id -Label 'stale Jobkeep.exe' }
        Start-Sleep -Milliseconds 500
    }
    foreach ($owner in Get-PortOwner -Port $ApiPort) {
        Stop-Tree -ProcessId ([int]$owner) -Label "whatever held :$ApiPort"
    }

    $out = Join-Path $LogDir 'api.log'
    $err = Join-Path $LogDir 'api.err.log'
    Write-Note "dotnet run -> $out"

    $proc = Start-Process -FilePath 'dotnet' `
        -ArgumentList 'run', '--project', $ApiProject, '--launch-profile', 'http' `
        -WorkingDirectory $Root `
        -RedirectStandardOutput $out -RedirectStandardError $err `
        -WindowStyle Hidden -PassThru

    # /applications, not /swagger: it exercises the database round trip, so a
    # 200 here means migrations applied and the connection string is right.
    # Swagger would answer before any of that was true.
    $ready = Wait-For -What "api ($ApiUrl)" -Seconds $TimeoutSeconds -Until {
        if ($proc.HasExited) { return $false }
        try {
            $r = Invoke-WebRequest -Uri "$ApiUrl/applications" -TimeoutSec 4 -SkipHttpErrorCheck
            return ($r.StatusCode -eq 200)
        } catch { return $false }
    } -OnFail {
        Write-Note "last lines of $out :"
        if (Test-Path $out) { Get-Content $out -Tail 15 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray } }
        if ((Test-Path $err) -and (Get-Item $err).Length -gt 0) {
            Write-Note "stderr:"
            Get-Content $err -Tail 15 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        }
    }

    if (-not $ready) { return $null }
    return $proc
}

# ---------------------------------------------------------------------------
# 4. The front end
# ---------------------------------------------------------------------------

function Start-Frontend {
    if (-not (Test-Path (Join-Path $WebDir 'node_modules'))) {
        Write-Note 'node_modules missing -- running npm install (once)'
        Push-Location $WebDir
        try {
            & npm install
            if ($LASTEXITCODE -ne 0) { Write-Fail 'npm install failed'; return $null }
        } finally { Pop-Location }
    }

    foreach ($owner in Get-PortOwner -Port $WebPort) {
        Stop-Tree -ProcessId ([int]$owner) -Label "whatever held :$WebPort"
    }

    $out = Join-Path $LogDir 'web.log'
    $err = Join-Path $LogDir 'web.err.log'
    Write-Note "npm run dev -> $out"

    # cmd.exe /c, because npm on Windows is a shim rather than an executable
    # Start-Process can launch directly.
    $proc = Start-Process -FilePath $env:ComSpec `
        -ArgumentList '/c', 'npm run dev' `
        -WorkingDirectory $WebDir `
        -RedirectStandardOutput $out -RedirectStandardError $err `
        -WindowStyle Hidden -PassThru

    # Vite pins the port with strictPort, so :5173 open means :5173 serving --
    # a silent fallback to 5174 would break every CORS preflight and read like
    # a React bug.
    $ready = Wait-For -What "front end ($WebUrl)" -Seconds 90 -Until {
        if ($proc.HasExited) { return $false }
        Test-PortOpen -Port $WebPort
    } -OnFail {
        if (Test-Path $out) { Get-Content $out -Tail 15 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray } }
        if ((Test-Path $err) -and (Get-Item $err).Length -gt 0) {
            Get-Content $err -Tail 15 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        }
    }

    if (-not $ready) { return $null }
    return $proc
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

if ($Stop) {
    Stop-Stack -IncludeDatabase:$StopDatabase
    exit 0
}

$launched = @{}

try {
    Write-Host ''
    Write-Host 'JobKeep -- local stack' -ForegroundColor White
    Write-Host ''

    # --- 1. Docker ---------------------------------------------------------
    Write-Step 'Docker'
    if (Test-DockerUp) {
        Write-Ok 'daemon running'
    } else {
        if (-not (Start-DockerDesktop)) {
            Write-Fail 'Docker is not running and Docker Desktop was not found. Start it and re-run.'
            exit 1
        }
        if (-not (Wait-For -What 'docker daemon' -Seconds 120 -Until { Test-DockerUp })) { exit 1 }
        Write-Ok 'daemon running'
    }

    # --- 2. Postgres -------------------------------------------------------
    Write-Step "Postgres (:$DbPort)"
    if (-not (Start-Database)) { exit 1 }
    Write-Ok 'accepting queries'

    # --- 3. API ------------------------------------------------------------
    Write-Step "API (:$ApiPort)"
    $api = Start-Api
    if (-not $api) { exit 1 }
    $launched['api'] = $api.Id
    Write-Ok "$ApiUrl  (swagger: $ApiUrl/swagger, graphql: $ApiUrl/graphql)"

    # --- 4. Front end ------------------------------------------------------
    if (-not $NoFrontend) {
        Write-Step "Front end (:$WebPort)"
        # No teardown here -- the finally block does it, and calling it twice
        # just prints the same lines twice.
        $web = Start-Frontend
        if (-not $web) { exit 1 }
        $launched['web'] = $web.Id
        Write-Ok $WebUrl
    }

    $launched | ConvertTo-Json | Set-Content $PidFile -Encoding utf8

    Write-Host ''
    Write-Host '  Up:' -ForegroundColor White
    Write-Host "    front end   $WebUrl"    -ForegroundColor Green
    Write-Host "    api         $ApiUrl"    -ForegroundColor Green
    Write-Host "    swagger     $ApiUrl/swagger"
    Write-Host "    graphql     $ApiUrl/graphql"
    Write-Host "    logs        $LogDir"
    Write-Host ''
    Write-Host '  Ctrl+C stops everything.' -ForegroundColor DarkGray
    Write-Host ''

    if (-not $NoBrowser -and -not $NoFrontend) {
        Start-Process $WebUrl | Out-Null
    }

    # Hold the console. Also notices if either side dies on its own, so a
    # crashed API doesn't sit there looking like it's still up.
    while ($true) {
        Start-Sleep -Seconds 2
        if ($api.HasExited) {
            Write-Fail 'the API exited -- last lines:'
            Get-Content (Join-Path $LogDir 'api.log') -Tail 20 |
                ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
            break
        }
        if ($web -and $web.HasExited) {
            Write-Fail 'the front end exited -- last lines:'
            Get-Content (Join-Path $LogDir 'web.log') -Tail 20 |
                ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
            break
        }
    }
} finally {
    # Runs on Ctrl+C too. The database is deliberately left up: it holds the
    # seeded data, costs nothing idle, and starting it is the slowest step.
    Write-Host ''
    Stop-Stack
}

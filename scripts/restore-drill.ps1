# Proves that a backup can be restored. Runs on the local compose stack, which must be up.
#   1. Puts a known device and entry into the source, then dumps it with scripts/backup.ps1.
#   2. Restores the dump into a scratch PostgreSQL of the same major version (own container, own port, own volume).
#   3. Compares row counts of every table and the SHA-256 of an ordered dump of entries, source against restore.
#   4. Starts the server image on the restored database and runs scripts/smoke.ps1 against it.
#   5. Shows what a restore does to a client: a device made after the dump is unknown (401), the client registers
#      again and sends its entry again.
# Source connection: PGHOST, PGPORT, PGUSER, PGPASSWORD, PGDATABASE (default: local compose database).
# The scratch container, its volume, the server container and the temp folder are removed at the end, also on failure.
# Usage: powershell -File scripts/restore-drill.ps1 [-SourceUrl http://127.0.0.1:8080]
# The drill leaves two devices and two entries named "drill ..." in the source database. There is no delete endpoint.
param(
    [string]$SourceUrl = "http://127.0.0.1:8080",
    [int]$ScratchPort = 5541,
    [int]$ServerPort = 8082
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot/common.ps1"

$SourceUrl = $SourceUrl.TrimEnd("/")
$scratchName = "journalstack-drill-db"
$scratchVolume = "journalstack-drill-data"
$serverName = "journalstack-drill-server"
$serverImage = "journalstack-drill-server"
$workDir = Join-Path ([IO.Path]::GetTempPath()) ("journalstack-drill-" + [guid]::NewGuid().ToString("N"))

function Fail($Step, $Message) {
    Write-Host "FAIL $Step : $Message"
    $script:failed = $true
    throw "drill stopped"
}

function Expect($Step, $Answer, $Want) {
    if ($Answer.Status -ne $Want) {
        Fail $Step "expected status $Want, got $($Answer.Status)"
    }
    Write-Host "ok   $Step"
}

function Remove-DrillThings {
    # Docker answers on stderr when a thing is already gone; that is not an error here.
    $ErrorActionPreference = "Continue"
    docker rm -f $scratchName 2>$null | Out-Null
    docker rm -f $serverName 2>$null | Out-Null
    docker volume rm -f $scratchVolume 2>$null | Out-Null
    docker rmi $serverImage 2>$null | Out-Null
    if (Test-Path $workDir) {
        Remove-Item -Recurse -Force $workDir
    }
}

# Row count of every table in one answer, and the hash of entries in a fixed order.
function Get-Facts($Image, $Pg, $Tables) {
    $countSql = ($Tables | ForEach-Object { "SELECT '$_', count(*) FROM $_" }) -join " UNION ALL "
    $counts = Invoke-Psql $Image $Pg ($countSql + " ORDER BY 1")
    $tableList = Invoke-Psql $Image $Pg "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1"

    $outName = "entries-" + [guid]::NewGuid().ToString("N") + ".txt"
    $env:PGPASSWORD = $Pg.Password
    Invoke-Docker run --rm -e PGPASSWORD -e PGTZ=UTC -v "${workDir}:/out" $Image psql -h (Get-DockerHost $Pg.Host) -p $Pg.Port -U $Pg.User -d $Pg.Database -o "/out/$outName" -c "COPY (SELECT * FROM entries ORDER BY id) TO STDOUT" | Out-Null
    $hash = (Get-FileHash -Algorithm SHA256 (Join-Path $workDir $outName)).Hash.ToLower()
    return @{ Tables = $tableList; Counts = $counts; Hash = $hash }
}

$failed = $false
$exitCode = 0
$total = [Diagnostics.Stopwatch]::StartNew()

try {
    Remove-DrillThings
    New-Item -ItemType Directory -Force $workDir | Out-Null

    $source = Get-PgSettings
    $major = Get-PgMajor $source
    $image = "postgres:$major-alpine"
    Write-Host "source is PostgreSQL $major; scratch database and tools use $image"

    if ((Send-Http $SourceUrl "GET" "/health/ready" $null $null).Status -ne 200) {
        Fail "source server" "$SourceUrl/health/ready did not answer 200. Is the compose stack up?"
    }

    # 1. Device A and its entry exist before the dump.
    $stamp = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $deviceA = Send-Http $SourceUrl "POST" "/api/v1/devices" $null $null
    Expect "source: register device A" $deviceA 201
    $tokenA = ($deviceA.Body | ConvertFrom-Json).token
    $idA = [guid]::NewGuid().ToString()
    $entryA = @{ id = $idA; text = "drill before dump $idA"; clientCreatedAt = $stamp } | ConvertTo-Json -Compress
    Expect "source: entry A" (Send-Http $SourceUrl "POST" "/api/v1/entries" $tokenA $entryA) 201

    # 2. Dump.
    $dumpFile = Join-Path $workDir "drill.dump"
    $dumpTimer = [Diagnostics.Stopwatch]::StartNew()
    & powershell -NoProfile -File "$PSScriptRoot/backup.ps1" -OutFile $dumpFile
    if ($LASTEXITCODE -ne 0) {
        Fail "backup" "backup.ps1 exited with $LASTEXITCODE"
    }
    $dumpTimer.Stop()
    $dumpBytes = (Get-Item $dumpFile).Length

    # Facts of the source are read right after the dump, before anything new is written to it.
    $tables = (Invoke-Psql $image $source "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1") -split "`n"
    $sourceFacts = Get-Facts $image $source $tables

    # 3. After the dump: device B and entry B exist only in the source.
    $deviceB = Send-Http $SourceUrl "POST" "/api/v1/devices" $null $null
    Expect "source: register device B (after the dump)" $deviceB 201
    $tokenB = ($deviceB.Body | ConvertFrom-Json).token
    $idB = [guid]::NewGuid().ToString()
    $entryB = @{ id = $idB; text = "drill after dump $idB"; clientCreatedAt = $stamp } | ConvertTo-Json -Compress
    Expect "source: entry B (after the dump)" (Send-Http $SourceUrl "POST" "/api/v1/entries" $tokenB $entryB) 201

    # 4. Scratch database of the same major, then the restore.
    $scratch = @{ Host = "127.0.0.1"; Port = "$ScratchPort"; User = $source.User; Database = $source.Database; Password = [guid]::NewGuid().ToString("N") }
    $env:POSTGRES_PASSWORD = $scratch.Password
    Invoke-Docker run -d --name $scratchName -e POSTGRES_USER=$($scratch.User) -e POSTGRES_DB=$($scratch.Database) -e POSTGRES_PASSWORD -p "127.0.0.1:${ScratchPort}:5432" -v "${scratchVolume}:/var/lib/postgresql/data" $image | Out-Null
    # The first start runs a temporary server that listens on the socket only, so ask over TCP.
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $scratchName pg_isready -h 127.0.0.1 -U $scratch.User -d $scratch.Database 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $ready = $true
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) {
        Fail "scratch database" "not ready after 60 seconds"
    }

    $restoreTimer = [Diagnostics.Stopwatch]::StartNew()
    $env:PGPASSWORD = $scratch.Password
    Invoke-Docker run --rm -e PGPASSWORD -v "${workDir}:/out" $image pg_restore --exit-on-error --no-owner --no-privileges -h (Get-DockerHost $scratch.Host) -p $scratch.Port -U $scratch.User -d $scratch.Database /out/drill.dump | Out-Null
    $restoreTimer.Stop()
    Write-Host "ok   restore finished"

    # 5. Compare.
    $restoredFacts = Get-Facts $image $scratch $tables
    $problems = @()
    if ($restoredFacts.Tables -ne $sourceFacts.Tables) {
        $problems += "table lists differ"
    }
    if ($restoredFacts.Counts -ne $sourceFacts.Counts) {
        $problems += "row counts differ"
        Write-Host "source counts:`n$($sourceFacts.Counts)"
        Write-Host "restored counts:`n$($restoredFacts.Counts)"
    }
    if ($restoredFacts.Hash -ne $sourceFacts.Hash) {
        $problems += "entries hash differs"
        Write-Host "source entries sha256:   $($sourceFacts.Hash)"
        Write-Host "restored entries sha256: $($restoredFacts.Hash)"
    }
    if ($problems.Count -gt 0) {
        Fail "compare" ($problems -join "; ")
    }
    Write-Host "ok   tables: $($tables -join ', ')"
    Write-Host "ok   row counts equal:"
    foreach ($line in ($sourceFacts.Counts -split "`n")) {
        Write-Host "       $line"
    }
    Write-Host "ok   entries sha256 equal: $($sourceFacts.Hash)"

    # 6. The server image on the restored database, then the smoke test.
    Invoke-Docker build -q -t $serverImage "$PSScriptRoot/../server" | Out-Null
    $env:PGPASSWORD = $scratch.Password
    Invoke-Docker run -d --name $serverName -p "127.0.0.1:${ServerPort}:8080" -e PGHOST=host.docker.internal -e PGPORT=$ScratchPort -e PGUSER=$($scratch.User) -e PGPASSWORD -e PGDATABASE=$($scratch.Database) $serverImage | Out-Null
    $restoredUrl = "http://127.0.0.1:$ServerPort"
    $up = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            if ((Send-Http $restoredUrl "GET" "/health/ready" $null $null).Status -eq 200) {
                $up = $true
                break
            }
        }
        catch {
        }
        Start-Sleep -Seconds 1
    }
    if (-not $up) {
        Fail "restored server" "did not become ready on $restoredUrl"
    }
    & powershell -NoProfile -File "$PSScriptRoot/smoke.ps1" -BaseUrl $restoredUrl
    if ($LASTEXITCODE -ne 0) {
        Fail "smoke test on the restored database" "smoke.ps1 exited with $LASTEXITCODE"
    }

    # 7. What the restore does to clients.
    $tokenAList = Send-Http $restoredUrl "GET" "/api/v1/entries" $tokenA $null
    Expect "restored: device A (before the dump) still has its token" $tokenAList 200
    if (@(($tokenAList.Body | ConvertFrom-Json).entries | Where-Object { $_.id -eq $idA }).Count -ne 1) {
        Fail "restored: entry A" "entry A is not in the restored list"
    }
    Write-Host "ok   restored: entry A is there"
    Expect "restored: device B (made after the dump) is unknown, answer is 401" (Send-Http $restoredUrl "GET" "/api/v1/entries" $tokenB $null) 401
    $again = Send-Http $restoredUrl "POST" "/api/v1/devices" $null $null
    Expect "restored: the client registers again" $again 201
    $newToken = ($again.Body | ConvertFrom-Json).token
    Expect "restored: the client sends entry B again, same id" (Send-Http $restoredUrl "POST" "/api/v1/entries" $newToken $entryB) 201
    Write-Host "     After a restore the client gets 401, registers again and sends every entry it has not confirmed again."
    Write-Host "     The entry id makes the second send safe. Unity EditMode test OutboxTests.ResetAllToPendingLiftsSyncedAndFailed"
    Write-Host "     covers the reset to Pending; the register-and-resend loop in JournalController was seen in Play check P13."

    $total.Stop()
    Write-Host ("dump: {0} bytes in {1:N1} s; restore: {2:N1} s; whole drill: {3:N1} s" -f $dumpBytes, $dumpTimer.Elapsed.TotalSeconds, $restoreTimer.Elapsed.TotalSeconds, $total.Elapsed.TotalSeconds)
    Write-Host "PASS"
}
catch {
    if (-not $failed) {
        Write-Host "FAIL unexpected : $($_.Exception.Message)"
    }
    $exitCode = 1
}
finally {
    Remove-DrillThings
}
exit $exitCode

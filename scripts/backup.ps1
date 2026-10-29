# Dumps the database with pg_dump -Fc into -OutFile.
# Connection: PGHOST, PGPORT, PGUSER, PGPASSWORD, PGDATABASE, the same names as the server. With none set it dumps
# the local compose database (port 5440) and takes the password from .env. It never prints the password.
# The dump tool runs from a postgres:<major> image, where <major> is read from the database with SHOW server_version.
# Usage: powershell -File scripts/backup.ps1 -OutFile backups/journalstack.dump
param(
    [Parameter(Mandatory = $true)][string]$OutFile
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot/common.ps1"

$fullPath = $OutFile
if (-not [IO.Path]::IsPathRooted($fullPath)) {
    $fullPath = Join-Path (Get-Location).Path $fullPath
}
$fullPath = [IO.Path]::GetFullPath($fullPath)
if (Test-Path $fullPath) {
    throw "$fullPath already exists. Pick a new name; a backup is never overwritten."
}
$folder = Split-Path $fullPath -Parent
New-Item -ItemType Directory -Force $folder | Out-Null
$fileName = Split-Path $fullPath -Leaf

$pg = Get-PgSettings
$major = Get-PgMajor $pg
$image = "postgres:$major-alpine"
Write-Host "database is PostgreSQL $major, dumping with $image"

$timer = [Diagnostics.Stopwatch]::StartNew()
$env:PGPASSWORD = $pg.Password
Invoke-Docker run --rm -e PGPASSWORD -v "${folder}:/out" $image pg_dump -Fc -h (Get-DockerHost $pg.Host) -p $pg.Port -U $pg.User -d $pg.Database -f "/out/$fileName" | Out-Null
$timer.Stop()

# The dump must exist, must not be empty, and must be readable by pg_restore.
if (-not (Test-Path $fullPath) -or (Get-Item $fullPath).Length -eq 0) {
    throw "pg_dump finished but $fullPath is missing or empty."
}
Invoke-Docker run --rm -v "${folder}:/out" $image pg_restore --list "/out/$fileName" | Out-Null

$bytes = (Get-Item $fullPath).Length
Write-Host ("ok   dump written: {0} ({1} bytes, {2:N1} s)" -f $fullPath, $bytes, $timer.Elapsed.TotalSeconds)

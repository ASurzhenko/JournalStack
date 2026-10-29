# Helpers shared by backup.ps1 and restore-drill.ps1. Dot-source it: . "$PSScriptRoot/common.ps1"
# Passwords stay in memory and in the PGPASSWORD environment variable of the running script. Nothing here prints them.

# Reads the connection from the same variable names as the server. With none set, it points at the local
# compose database, never at a hosted one. A hosted run sets all five variables on purpose.
function Get-PgSettings {
    $localDefaults = $false
    if (-not $env:PGHOST) {
        $localDefaults = $true
    }
    $password = $env:PGPASSWORD
    if (-not $password) {
        $envFile = Join-Path $PSScriptRoot "../.env"
        if (Test-Path $envFile) {
            $line = Get-Content $envFile | Where-Object { $_ -match '^PGPASSWORD=.+' } | Select-Object -First 1
            if ($line) {
                $password = $line.Substring("PGPASSWORD=".Length)
            }
        }
    }
    if (-not $password) {
        throw "PGPASSWORD is not set and the .env file has none."
    }
    if ($localDefaults) {
        return @{ Host = "127.0.0.1"; Port = "5440"; User = "journal"; Database = "journalstack"; Password = $password }
    }
    foreach ($name in "PGPORT", "PGUSER", "PGDATABASE") {
        if (-not (Get-Item "env:$name" -ErrorAction SilentlyContinue)) {
            throw "PGHOST is set, so $name must be set too."
        }
    }
    return @{ Host = $env:PGHOST; Port = $env:PGPORT; User = $env:PGUSER; Database = $env:PGDATABASE; Password = $password }
}

# Inside a container, 127.0.0.1 is the container itself. This is how Docker Desktop reaches the machine.
function Get-DockerHost($PgHost) {
    if ($PgHost -in "127.0.0.1", "localhost", "::1") {
        return "host.docker.internal"
    }
    return $PgHost
}

function Invoke-Docker {
    $output = & docker @args
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($args[0]) failed with exit code $LASTEXITCODE"
    }
    return $output
}

# Runs one SQL statement with psql from the given image and returns the output as one trimmed string.
function Invoke-Psql($Image, $Pg, $Sql) {
    $env:PGPASSWORD = $Pg.Password
    $output = Invoke-Docker run --rm -e PGPASSWORD -e PGTZ=UTC $Image psql -h (Get-DockerHost $Pg.Host) -p $Pg.Port -U $Pg.User -d $Pg.Database -Atc $Sql
    return (($output | ForEach-Object { "$_" }) -join "`n").Trim()
}

# The major version of the running server, read from the server itself. Any recent psql can ask this question,
# so the probe image is the newest one; the answer picks the image that is used for the real work.
function Get-PgMajor($Pg) {
    $probeImage = "postgres:alpine"
    $version = Invoke-Psql $probeImage $Pg "SHOW server_version"
    if ($version -notmatch '^(\d+)(\.|$| )') {
        throw "Could not read the PostgreSQL version. SHOW server_version answered: '$version'"
    }
    return $Matches[1]
}

# Returns Status and Body for any answer, including 4xx and 5xx.
function Send-Http($BaseUrl, $Method, $Path, $Token, $Json) {
    $params = @{ Uri = "$BaseUrl$Path"; Method = $Method; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($Token) {
        $params["Headers"] = @{ Authorization = "Bearer $Token" }
    }
    if ($Json) {
        $params["Body"] = $Json
        $params["ContentType"] = "application/json"
    }
    try {
        $r = Invoke-WebRequest @params
        return @{ Status = [int]$r.StatusCode; Body = $r.Content }
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) {
            throw
        }
        $body = $_.ErrorDetails.Message
        if (-not $body) {
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
            $body = $reader.ReadToEnd()
        }
        return @{ Status = [int]$response.StatusCode; Body = $body }
    }
}

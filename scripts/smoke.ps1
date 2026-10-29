# Smoke test for a running JournalStack server (local or hosted).
# Usage: powershell -File scripts/smoke.ps1 -BaseUrl http://127.0.0.1:8081
# Leaves one device and one entry named "smoke test ..." in the database. There is no delete endpoint.
# Never prints the device token or any header.
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd("/")

# Returns Status and Body for any answer, including 4xx and 5xx.
function Send($Method, $Path, $Token, $Json) {
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

function Fail($Step, $Message) {
    Write-Host "FAIL $Step : $Message"
    exit 1
}

function ExpectStatus($Step, $Answer, $Want) {
    if ($Answer.Status -ne $Want) {
        Fail $Step "expected status $Want, got $($Answer.Status)"
    }
    Write-Host "ok   $Step"
}

# 1. Health.
ExpectStatus "GET /health" (Send "GET" "/health" $null $null) 200
ExpectStatus "GET /health/ready" (Send "GET" "/health/ready" $null $null) 200

# 2. Register a device. The token stays in this variable.
$registered = Send "POST" "/api/v1/devices" $null $null
ExpectStatus "POST /api/v1/devices" $registered 201
$token = ($registered.Body | ConvertFrom-Json).token

# 3. First entry.
$id = [guid]::NewGuid().ToString()
$now = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
$entry = @{ id = $id; text = "smoke test $id"; clientCreatedAt = $now } | ConvertTo-Json -Compress
$first = Send "POST" "/api/v1/entries" $token $entry
ExpectStatus "POST /api/v1/entries (new)" $first 201

# 4. The same body again is a retry: 200 and the same entry.
$retry = Send "POST" "/api/v1/entries" $token $entry
ExpectStatus "POST /api/v1/entries (retry)" $retry 200
if ($retry.Body -ne $first.Body) {
    Fail "retry body" "the retry answered a different entry"
}

# 5. The id is listed once.
$list = Send "GET" "/api/v1/entries" $token $null
ExpectStatus "GET /api/v1/entries" $list 200
$found = @(($list.Body | ConvertFrom-Json).entries | Where-Object { $_.id -eq $id })
if ($found.Count -ne 1) {
    Fail "list" "expected the id once, found $($found.Count)"
}

# 6. The same id with other text is refused, and the body says why.
$changed = @{ id = $id; text = "changed $id"; clientCreatedAt = $now } | ConvertTo-Json -Compress
$conflict = Send "POST" "/api/v1/entries" $token $changed
ExpectStatus "POST /api/v1/entries (changed text)" $conflict 409
$wantCode = "id_conflict"
$code = ($conflict.Body | ConvertFrom-Json).code
if ($code -ne $wantCode) {
    Fail "conflict code" "expected $wantCode, got $code"
}
Write-Host "ok   conflict code"

Write-Host "PASS"

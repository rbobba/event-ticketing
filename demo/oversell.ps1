<#
.SYNOPSIS
    Tries to oversell a tier, live, against a running instance.

.DESCRIPTION
    Creates an event with a deliberately small tier, then fires far more concurrent
    purchase requests than there are tickets — every one of them a real HTTP request,
    in flight at the same time, against the real database.

    The invariant under test is INV-1: tickets issued for a tier never exceed its
    allocation. The script checks it two ways, because checking one is not enough:
    the tier's remaining count, and the number of ticket rows actually issued. A tier
    reading zero while too many tickets exist is still an oversell.

    This is the same property AC_2_3 asserts in the integration suite. This script
    exists so it can be watched happening rather than read about.

.EXAMPLE
    .\demo\oversell.ps1
    .\demo\oversell.ps1 -Tickets 50 -Buyers 200
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:5076',
    [int]    $Tickets = 50,
    [int]    $Buyers  = 200
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Net.Http

# Windows PowerShell caps outbound connections per host at 2 by default, which would
# quietly serialise the requests and make this prove nothing at all.
[System.Net.ServicePointManager]::DefaultConnectionLimit = [Math]::Max($Buyers, 200)

Write-Host ""
Write-Host "Attempting to oversell: $Buyers concurrent buyers, $Tickets tickets" -ForegroundColor Cyan
Write-Host ""

try {
    $null = Invoke-RestMethod -Method Get -Uri "$BaseUrl/health/ready" -TimeoutSec 5
}
catch {
    Write-Host "The API is not answering on $BaseUrl." -ForegroundColor Red
    Write-Host "Start it with:  dotnet run --project src/Ticketing.Api" -ForegroundColor Red
    exit 1
}

$definition = @{
    name          = "Studio Session - $Tickets seats"
    description   = 'A deliberately small room, created to be fought over.'
    venue         = @{ name = 'Studio B'; timeZoneId = 'America/Chicago' }
    date          = '2027-02-20'
    time          = '20:00:00'
    totalCapacity = $Tickets
    tiers         = @(@{ name = 'Studio Floor'; price = 80.00; allocation = $Tickets })
}

$show = Invoke-RestMethod -Method Post -Uri "$BaseUrl/v1/events" `
                          -Body ($definition | ConvertTo-Json -Depth 6) `
                          -ContentType 'application/json; charset=utf-8'

$tierId = $show.tiers[0].id
Write-Host ("  event  {0}" -f $show.id) -ForegroundColor DarkGray
Write-Host ("  tier   {0}  allocation {1}" -f $tierId, $Tickets) -ForegroundColor DarkGray
Write-Host ""

$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(60)

$payload = @{ pricingTierId = $tierId; quantity = 1 } | ConvertTo-Json
$uri = "$BaseUrl/v1/events/$($show.id)/orders"

# Build every request first, so the loop that starts them does as little work as
# possible between one send and the next.
$requests = New-Object 'System.Collections.Generic.List[System.Net.Http.HttpRequestMessage]'

for ($i = 0; $i -lt $Buyers; $i++) {
    $message = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uri)
    $message.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')

    # Each buyer is a different person, so each gets its own key. Sharing one would
    # make 199 of these replays of the first, and would test idempotency instead.
    $null = $message.Headers.Add('Idempotency-Key', [guid]::NewGuid().ToString())

    $requests.Add($message)
}

Write-Host "  releasing $Buyers requests..." -ForegroundColor Yellow

$timer = [System.Diagnostics.Stopwatch]::StartNew()

$tasks = New-Object 'System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]'
foreach ($message in $requests) {
    $tasks.Add($client.SendAsync($message))
}

[System.Threading.Tasks.Task]::WaitAll($tasks.ToArray())
$timer.Stop()

$created = 0
$conflict = 0
$other = @{}

foreach ($task in $tasks) {
    $code = [int] $task.Result.StatusCode

    switch ($code) {
        201     { $created++ }
        409     { $conflict++ }
        default {
            if (-not $other.ContainsKey($code)) { $other[$code] = 0 }
            $other[$code]++
        }
    }

    $task.Result.Dispose()
}

$client.Dispose()

$availability = Invoke-RestMethod -Method Get -Uri "$BaseUrl/v1/events/$($show.id)/availability"
$summary = Invoke-RestMethod -Method Get -Uri "$BaseUrl/v1/events/$($show.id)/sales-summary"

$remaining = $availability.tiers[0].remaining
$sold = $summary.tiers[0].sold

Write-Host ""
Write-Host ("  finished in {0:N0} ms" -f $timer.Elapsed.TotalMilliseconds) -ForegroundColor DarkGray
Write-Host ""
Write-Host ("  201 Created   {0,5}   tickets issued" -f $created)
Write-Host ("  409 Conflict  {0,5}   told the tier was sold out" -f $conflict)

foreach ($code in $other.Keys) {
    Write-Host ("  {0}           {1,5}   unexpected" -f $code, $other[$code]) -ForegroundColor Red
}

Write-Host ""
Write-Host ("  allocation    {0,5}" -f $Tickets)
Write-Host ("  sold          {0,5}   (from the sales summary)" -f $sold)
Write-Host ("  remaining     {0,5}   (from availability)" -f $remaining)
Write-Host ""

# Three independent things have to line up. Any one of them alone could be satisfied
# by a system that is quietly wrong.
$ok = ($created -eq $Tickets) -and
      ($sold -eq $Tickets) -and
      ($remaining -eq 0) -and
      (($created + $conflict) -eq $Buyers)

if ($ok) {
    Write-Host "  INV-1 holds: exactly $Tickets tickets issued, no more." -ForegroundColor Green
    Write-Host "  Every losing request was told so; none of them failed." -ForegroundColor Green
}
else {
    Write-Host "  INV-1 VIOLATED - the numbers above do not agree." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  Now try to break it below the application, in psql:" -ForegroundColor DarkGray
Write-Host ("    UPDATE pricing_tiers SET remaining = -1 WHERE id = '{0}';" -f $tierId) -ForegroundColor DarkGray
Write-Host ""

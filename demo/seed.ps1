<#
.SYNOPSIS
    Fills a running instance with realistic events, tiers and orders.

.DESCRIPTION
    Everything goes in through the public API — no direct database writes, no EF
    seeding in a migration. If the API would reject it, this script cannot create it,
    so the seeded data is always data the system considers valid.

    Safe to run more than once; each run creates a fresh set of events.

.EXAMPLE
    .\demo\seed.ps1
    .\demo\seed.ps1 -BaseUrl http://localhost:5099
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:5076'
)

$ErrorActionPreference = 'Stop'

function Invoke-Api {
    param(
        [string] $Method,
        [string] $Path,
        $Body,
        [hashtable] $Headers = @{}
    )

    $uri = "$BaseUrl$Path"
    $json = $null

    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 6
    }

    return Invoke-RestMethod -Method $Method -Uri $uri -Body $json `
                             -ContentType 'application/json; charset=utf-8' `
                             -Headers $Headers
}

function New-Event {
    param([hashtable] $Definition)

    $created = Invoke-Api -Method Post -Path '/v1/events' -Body $Definition
    Write-Host ("  created  {0}" -f $created.name) -ForegroundColor Green
    return $created
}

function New-Order {
    param([guid] $EventId, [guid] $TierId, [int] $Quantity)

    # A fresh key per call. Reusing one would make the second call a replay, which is
    # correct behaviour and not what we want while seeding.
    $headers = @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }

    return Invoke-Api -Method Post -Path "/v1/events/$EventId/orders" `
                      -Body @{ pricingTierId = $TierId; quantity = $Quantity } `
                      -Headers $headers
}

Write-Host ""
Write-Host "Seeding $BaseUrl" -ForegroundColor Cyan
Write-Host ""

try {
    $null = Invoke-RestMethod -Method Get -Uri "$BaseUrl/health/ready" -TimeoutSec 5
}
catch {
    Write-Host "The API is not answering on $BaseUrl." -ForegroundColor Red
    Write-Host "Start it with:  dotnet run --project src/Ticketing.Api" -ForegroundColor Red
    exit 1
}

$definitions = @(
    @{
        name          = 'Midwest Jazz Collective - Autumn Session'
        description   = 'Four sets across one evening, with a standing floor and a seated balcony.'
        venue         = @{ name = 'Riverside Hall'; timeZoneId = 'America/Chicago' }
        date          = '2026-11-14'
        time          = '19:30:00'
        totalCapacity = 500
        tiers         = @(
            @{ name = 'General Admission'; price = 45.00; allocation = 350 },
            @{ name = 'Balcony';           price = 65.00; allocation = 100 },
            @{ name = 'Front Row';         price = 120.00; allocation = 50 }
        )
    },
    @{
        name          = 'Cascade Trail Half Marathon'
        description   = 'A single-loop trail half marathon. Entry includes timing and a finish photo.'
        venue         = @{ name = 'Mill Creek Start Line'; timeZoneId = 'America/Los_Angeles' }
        date          = '2027-04-17'
        time          = '07:00:00'
        totalCapacity = 1200
        tiers         = @(
            @{ name = 'Early Entry';    price = 70.00; allocation = 400 },
            @{ name = 'Standard Entry'; price = 95.00; allocation = 700 },
            @{ name = 'Charity Entry';  price = 150.00; allocation = 100 }
        )
    },
    @{
        name          = 'Winter Lantern Festival'
        description   = 'An evening walk through the lantern installations, timed entry.'
        venue         = @{ name = 'Harbour Park'; timeZoneId = 'America/New_York' }
        date          = '2027-01-09'
        time          = '17:45:00'
        totalCapacity = 800
        tiers         = @(
            @{ name = 'Standard';          price = 30.00; allocation = 600 },
            @{ name = 'Family Four-Pack';  price = 100.00; allocation = 200 }
        )
    }
)

$events = @()
foreach ($definition in $definitions) {
    $events += New-Event -Definition $definition
}

Write-Host ""
Write-Host "  placing orders so the sales summary has something in it" -ForegroundColor DarkGray

# Jazz: healthy sales across all three tiers.
$jazz = $events[0]
$null = New-Order -EventId $jazz.id -TierId $jazz.tiers[0].id -Quantity 4
$null = New-Order -EventId $jazz.id -TierId $jazz.tiers[0].id -Quantity 2
$null = New-Order -EventId $jazz.id -TierId $jazz.tiers[1].id -Quantity 6
$null = New-Order -EventId $jazz.id -TierId $jazz.tiers[2].id -Quantity 2

# Half marathon: the cheap tier moving fastest, which is the realistic shape.
$race = $events[1]
$null = New-Order -EventId $race.id -TierId $race.tiers[0].id -Quantity 8
$null = New-Order -EventId $race.id -TierId $race.tiers[0].id -Quantity 3
$null = New-Order -EventId $race.id -TierId $race.tiers[1].id -Quantity 2

# Lantern festival: left with no sales on purpose, so the summary can be shown
# returning zeroes rather than a 404 (AC-4.2).

Write-Host ""
Write-Host "Seeded." -ForegroundColor Cyan
Write-Host ""

foreach ($created in $events) {
    Write-Host $created.name -ForegroundColor White
    Write-Host ("  event        {0}" -f $created.id)
    Write-Host ("  availability {0}/v1/events/{1}/availability" -f $BaseUrl, $created.id)
    Write-Host ("  summary      {0}/v1/events/{1}/sales-summary" -f $BaseUrl, $created.id)

    foreach ($tier in $created.tiers) {
        Write-Host ("  tier         {0,-20} {1,10}  {2}" -f $tier.name, $tier.price, $tier.id) -ForegroundColor DarkGray
    }

    Write-Host ""
}

# Written so the other demo steps, and any .http file, can pick ids up without
# copying them by hand.
$idFile = Join-Path $PSScriptRoot 'last-seed.json'
$events | ConvertTo-Json -Depth 6 | Set-Content -Path $idFile -Encoding UTF8
Write-Host "Ids written to $idFile" -ForegroundColor DarkGray
Write-Host ""

#Requires -Version 7.5

<#
.SYNOPSIS
Rebuilds energy-star-monitors.csv, the table of certified monitors' measured power that PowerLedger ships, from the US
EPA's list of ENERGY STAR certified displays.

.DESCRIPTION
Downloads every monitor in the public dataset "ENERGY STAR Certified Displays" (data.energystar.gov, qbg3-d468; signage
displays are left out), keeps what PowerLedger needs to recognise a monitor and work out its draw, and writes the table
next to this script. Nothing is fetched at runtime: run this again to refresh the table, then commit it and update the
date in README.md.

A monitor is dropped when it has no brand, no model number or name, no screen size, no resolution that reads as
"width x height", or no on-mode watts; when its on-mode watts are under 1 W; or when they are more than three times the
median of the monitors of its size, rounded to the nearest inch. The last catches typing errors like a 24" monitor listed
at 151.3 W.

Model numbers and names are written exactly as the list has them, since the App normalises both sides when it matches.
The alternatives column holds the other identifiers a monitor is listed under, joined with '|'. They are the pieces of its
additional model information, model number and model name, split at commas, semicolons, slashes, brackets and the words
"and" and "or", that contain only letters, digits, '-', '_' and the wildcard characters '*', '?' and '#', with at least
one letter and one digit, and that aren't its own model number or name. That keeps "U2723QX" and "S24D400GA*", and drops
the prose around them ("* can be any alphanumeric character"). Wildcards, including runs of X standing for any
character, are kept as written.

The table is sorted by brand and then model number, ignoring case, so a refresh diffs cleanly. Columns: brand,
model_number, model_name, alternatives, inches, width, height, panel, on_w, sleep_w, off_w, max_nits (the maximum
luminance, cd/m²), hdr (the DisplayHDR tier, empty where the list says N/A), certified (yyyy-MM-dd). Numbers are
written with a '.' and no trailing zeros. The file is UTF-8 without a BOM, one CRLF-ended line a monitor, with RFC 4180
quoting.

Needs PowerShell 7.5 or later (ConvertFrom-Json -DateKind).

.EXAMPLE
pwsh assets\monitors\make-monitor-table.ps1
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$invariant = [Globalization.CultureInfo]::InvariantCulture

$endpoint = 'https://data.energystar.gov/resource/qbg3-d468.json'
$pageSize = 5000
$table = Join-Path $PSScriptRoot 'energy-star-monitors.csv'
$columns = 'brand', 'model_number', 'model_name', 'alternatives', 'inches', 'width', 'height', 'panel', 'on_w', 'sleep_w',
    'off_w', 'max_nits', 'hdr', 'certified'

# ---- Download, a page at a time, in a fixed order ----
$records = [Collections.Generic.List[object]]::new()
do {
    $url = '{0}?$where={1}&$order=pd_id&$limit={2}&$offset={3}' -f $endpoint, [uri]::EscapeDataString("display_type='Monitor'"), $pageSize, $records.Count
    # -DateKind String keeps date_certified as the text the list sends, not a local DateTime.
    $page = @((Invoke-WebRequest $url).Content | ConvertFrom-Json -DateKind String)
    $records.AddRange($page)
} while ($page.Count -eq $pageSize)

# ---- Reading the fields ----
$separators = [regex]::new('[,;/()]|\s+(?:and|or)\s+', 'IgnoreCase')
$identifier = [regex]'^(?=.*[A-Za-z])(?=.*[0-9])[A-Za-z0-9_*?#-]+$'
$resolution = [regex]'^\s*([0-9]+)\s*[xX]\s*([0-9]+)\s*$'

# A number, or $null when the field is missing or isn't one.
function Read-Number($Text) {
    $value = [decimal]0
    if ([decimal]::TryParse("$Text", [Globalization.NumberStyles]::Float, $invariant, [ref]$value)) { $value } else { $null }
}

function Format-Number($Value) { if ($null -eq $Value) { '' } else { $Value.ToString('0.##########', $invariant) } }

# The identifiers a monitor is also listed under, in the order the list gives them, each once.
function Get-Alternatives($Record) {
    $kept = [Collections.Generic.List[string]]::new()
    foreach ($text in $Record.model_number, $Record.model_name, $Record.additional_model_information) {
        if (-not $text) { continue }
        foreach ($piece in $separators.Split($text)) {
            $token = $piece.Trim()
            if ($identifier.IsMatch($token) -and $token -cne $Record.model_number -and $token -cne $Record.model_name -and -not $kept.Contains($token)) {
                $kept.Add($token)
            }
        }
    }
    $kept -join '|'
}

# RFC 4180: quote a field holding a comma, a quote or a line break, doubling its quotes.
function Format-Field([string]$Text) { if ($Text -match '[",\r\n]') { '"' + $Text.Replace('"', '""') + '"' } else { $Text } }

# ---- Clean ----
$dropped = [ordered]@{ 'not a monitor' = 0; 'no brand' = 0; 'no model number or name' = 0; 'no screen size' = 0; 'no resolution' = 0; 'no on-mode watts' = 0; 'under 1 W on' = 0 }
$monitors = foreach ($record in $records) {
    $inches = Read-Number $record.screen_size_inches
    $pixels = $resolution.Match("$($record.native_resolution_pixels)")
    $onW = Read-Number $record.on_mode_power_watts
    $reason =
        if ($record.display_type -ne 'Monitor') { 'not a monitor' }
        elseif ([string]::IsNullOrWhiteSpace($record.brand_name)) { 'no brand' }
        elseif ([string]::IsNullOrWhiteSpace($record.model_number) -and [string]::IsNullOrWhiteSpace($record.model_name)) { 'no model number or name' }
        elseif ($null -eq $inches -or $inches -le 0) { 'no screen size' }
        elseif (-not $pixels.Success -or [int]$pixels.Groups[1].Value -le 0 -or [int]$pixels.Groups[2].Value -le 0) { 'no resolution' }
        elseif ($null -eq $onW) { 'no on-mode watts' }
        elseif ($onW -lt 1) { 'under 1 W on' }
    if ($reason) { $dropped[$reason]++; continue }

    $hdr = "$($record.high_dynamic_range_hdr)"
    [pscustomobject]@{
        Size   = [int][decimal]::Round($inches, 0, [MidpointRounding]::AwayFromZero)
        OnW    = $onW
        Label  = "$($record.brand_name) $($record.model_number), $(Format-Number $inches)`", $(Format-Number $onW) W"
        Fields = @(
            "$($record.brand_name)", "$($record.model_number)", "$($record.model_name)", (Get-Alternatives $record)
            (Format-Number $inches), $pixels.Groups[1].Value, $pixels.Groups[2].Value, "$($record.panel_type)"
            (Format-Number $onW), (Format-Number (Read-Number $record.sleep_mode_power_watts)), (Format-Number (Read-Number $record.off_mode_power_watts))
            (Format-Number (Read-Number $record.maximum_luminance_candelas)), $(if ($hdr -eq 'N/A') { '' } else { $hdr })
            $(if ("$($record.date_certified)" -match '^([0-9]{4}-[0-9]{2}-[0-9]{2})') { $Matches[1] } else { '' })
        )
    }
}

# More than three times the median of the same size is a typing error, not a monitor.
$medians = @{}
foreach ($group in $monitors | Group-Object Size) {
    $watts = @($group.Group.OnW | Sort-Object)
    $middle = [Math]::Floor($watts.Count / 2)
    $medians[[int]$group.Name] = if ($watts.Count % 2) { $watts[$middle] } else { ($watts[$middle - 1] + $watts[$middle]) / 2 }
}
$outliers = @($monitors | Where-Object { $_.OnW -gt 3 * $medians[$_.Size] })
$monitors = @($monitors | Where-Object { $_.OnW -le 3 * $medians[$_.Size] })

# ---- Write, sorted by brand then model number (ignoring case), then by the whole line so equal keys sort the same way ----
$lines = [string[]]@($monitors | ForEach-Object { ($_.Fields | ForEach-Object { Format-Field $_ }) -join ',' })
$keys = [string[]]@(for ($i = 0; $i -lt $monitors.Count; $i++) {
    $monitors[$i].Fields[0].ToUpperInvariant() + "`0" + $monitors[$i].Fields[1].ToUpperInvariant() + "`0" + $lines[$i]
})
[Array]::Sort($keys, $lines, [StringComparer]::Ordinal)
$csv = (@($columns -join ',') + $lines) -join "`r`n"
[IO.File]::WriteAllText($table, $csv + "`r`n", [Text.UTF8Encoding]::new($false))

"Downloaded $($records.Count) monitors from ENERGY STAR Certified Displays."
foreach ($reason in $dropped.Keys) { if ($dropped[$reason]) { "Dropped $($dropped[$reason]): $reason." } }
foreach ($outlier in $outliers) { "Dropped $($outlier.Label): over three times the $($medians[$outlier.Size]) W median at $($outlier.Size)`"." }
"Wrote $($monitors.Count) monitors to $(Split-Path $table -Leaf)."

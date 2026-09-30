# Screenshots reference.html in headless Edge for each case, into $Out, with a throwaway profile (never the user's).
# The engine's committed references (tests/PowerLedger.App.Tests/Aero/LiquidGlass/Reference) came from this script.
param([string]$Out = "$env:TEMP\liquid-glass-reference")
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$page = (Resolve-Path "$PSScriptRoot\reference.html").Path.Replace('\', '/')
New-Item -ItemType Directory -Force $Out | Out-Null
$cases = @(
    @{ Name = 'a'; Box = '200,150,320,200'; Scale = '1' },     # the source card's proportions
    @{ Name = 'b'; Box = '61,233,173,97'; Scale = '1' },       # odd size, odd place
    @{ Name = 'c'; Box = '80,60,560,380'; Scale = '1' },       # large: every lattice cell of the first octave
    @{ Name = 'd'; Box = '200,150,320,200'; Scale = '1.5' },   # 150 % display scaling
    @{ Name = 'e'; Box = '41,37,250,150'; Scale = '1.25' }     # 125 %, odd place
)
foreach ($case in $cases) {
    foreach ($mode in 'dispx', 'dispy', 'full') {
        & $edge --headless=new "--user-data-dir=$Out\profile" --force-color-profile=srgb "--force-device-scale-factor=$($case.Scale)" `
            --hide-scrollbars --window-size=720,500 "--screenshot=$Out\$($case.Name)-$mode.png" "file:///$page#$mode,$($case.Box)" 2>$null | Out-Null
    }
}
Get-ChildItem $Out -Filter *.png | Select-Object Name, Length

#Requires -Version 7

<#
.SYNOPSIS
Installs PowerLedger on this machine with its installer, checks what it installed, then upgrades, removes and reinstalls it.

.DESCRIPTION
Every step needs an elevated PowerShell 7; a step run without one fails at once. Build the installers first with
installer\build.ps1 -TestVariants. Each check is appended to <Results>\results.jsonl as {step, check, ok, detail, time}
and printed as PASS, FAIL or SKIP, and the exit code is the number of failures. Setup and uninstall logs go to
<Results>\<step>-setup.log and <step>-uninstall.log; the interactive steps write the text of every window they see to
<Results>\dialogs.log.

The default steps are silent, so CI can run them: Preflight, Install, Service, Data, Recovery, ServiceStop, Upgrade,
UninstallKeep, Reinstall, Cleanup. Three more need a desktop. InstallWizard and UninstallDelete start setup or the
uninstaller and answer it themselves (InstallWizard installs with the wizard, as somebody new to PowerLedger would, for a
clean Windows such as Windows Sandbox); DriveWizard waits for a setup somebody else started, unelevated say, and drives
it to the end.

Preflight stops the run when C:\ProgramData\PowerLedger exists, since that may be somebody's history. Cleanup deletes the
folder only when this run's Preflight found none there.

.EXAMPLE
./installer/test-installer.ps1

.EXAMPLE
./installer/test-installer.ps1 -Step Preflight, InstallWizard, Service, Data, UninstallDelete, Cleanup
#>
param(
    [ValidateSet('Preflight', 'Install', 'Service', 'Data', 'Recovery', 'ServiceStop', 'Upgrade', 'UninstallKeep', 'Reinstall',
        'UninstallDelete', 'InstallWizard', 'DriveWizard', 'Cleanup')]
    [string[]]$Step = @('Preflight', 'Install', 'Service', 'Data', 'Recovery', 'ServiceStop', 'Upgrade', 'UninstallKeep',
        'Reinstall', 'Cleanup'),
    [string]$Setup,
    [string]$Upgrade,
    [string]$Results,
    [int]$WizardTimeout = 600
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # sc.exe's exit codes are read, not thrown
$root = Split-Path -Parent $PSScriptRoot

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
$parsed = [version]$version
$patched = '{0}.{1}.{2}' -f $parsed.Major, $parsed.Minor, ($parsed.Build + 1)   # the upgrade build's version
$output = Join-Path $PSScriptRoot 'output'
if (-not $Setup) { $Setup = Join-Path $output "PowerLedger-$version-setup.exe" }
if (-not $Upgrade) { $Upgrade = Join-Path $output "test\PowerLedger-$patched-setup.exe" }
if (-not $Results) { $Results = Join-Path $output 'test-results' }
$Results = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Results)
$null = New-Item -ItemType Directory -Force $Results

$ServiceName = 'PowerLedger'
$AppDir = Join-Path $env:ProgramFiles 'PowerLedger'
$AppExe = Join-Path $AppDir 'PowerLedger.exe'
$ServiceExe = Join-Path $AppDir 'Service\PowerLedger.Service.exe'
$DataDir = Join-Path $env:ProgramData 'PowerLedger'
$Database = Join-Path $DataDir 'power.db'
$UninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E496D40-C77E-4F75-8C93-ED9D1E8BAF20}_is1'
$Shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PowerLedger.lnk'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$EventSourceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\PowerLedger'
$PipeScript = Join-Path $root 'scripts\pipe-status.ps1'
$Silent = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
$SetupProcess = 'PowerLedger-*-setup*'          # the installer and the setup.tmp it runs, which owns the wizard
$UninstallProcess = 'unins*', '_unins*'         # unins000.exe and the copy of itself it hands over to
$ExpectedRules = @(                             # what the service puts on the data folder
    'S-1-5-18 Allow FullControl (ContainerInherit, ObjectInherit; None)'
    'S-1-5-32-544 Allow FullControl (ContainerInherit, ObjectInherit; None)'
    'S-1-5-32-545 Allow ReadAndExecute, Synchronize (ContainerInherit, ObjectInherit; None)'
)
$MessageBoxIds = @{ Yes = '6'; No = '7'; OK = '1', '2'; Cancel = '2' }   # control ids of a message box's buttons
$PeMachines = @{ X64 = 0x8664; Arm64 = 0xAA64 }  # a PE header's machine field, for each Windows PowerLedger has a build for

$ResultsFile = Join-Path $Results 'results.jsonl'
$PreflightMarker = Join-Path $Results 'preflight-found-no-data.txt'
$SavedHistory = Join-Path $Results 'history-before-uninstall.json'
$Started = Get-Date
$Records = [Collections.Generic.List[object]]::new()
$Seen = [Collections.Generic.HashSet[string]]::new()
$StopRun = $false

# ---- Recording ----

function Write-Result([string]$StepName, [string]$CheckName, [bool]$Ok, [string]$Detail, [switch]$Skipped) {
    $record = [pscustomobject][ordered]@{ step = $StepName; check = $CheckName; ok = $Ok; detail = $Detail; time = (Get-Date).ToString('o') }
    $Records.Add($record)
    Add-Content -LiteralPath $ResultsFile -Value ($record | ConvertTo-Json -Compress) -Encoding utf8
    $label, $color = if ($Skipped) { 'SKIP', 'Yellow' } elseif ($Ok) { 'PASS', 'Green' } else { 'FAIL', 'Red' }
    Write-Host ('{0} {1} / {2}{3}' -f $label, $StepName, $CheckName, $(if ($Detail) { " - $Detail" })) -ForegroundColor $color
}

# Runs one check. It passes unless the body throws; what the body outputs is the detail recorded.
function Check([string]$StepName, [string]$CheckName, [scriptblock]$Body) {
    try {
        $detail = (& $Body) -join '; '
        $ok = $true
    }
    catch {
        $detail = $_.Exception.Message
        $ok = $false
    }
    Write-Result $StepName $CheckName $ok $detail
}

# Throws the detail when the condition fails and returns it when it holds, so the detail should say what was seen.
function Assert($Condition, [string]$Detail) {
    if (-not $Condition) { throw $Detail }
    $Detail
}

function Skip([string]$StepName, [string]$CheckName, [string]$Why) { Write-Result $StepName $CheckName $true "skipped: $Why" -Skipped }

# ---- Waiting and processes ----

# Polls every second until the condition holds or the time is up, and says which. A condition that throws has not held.
function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        $met = try { [bool](& $Condition) } catch { $false }
        if ($met -or (Get-Date) -ge $deadline) { return $met }
        Start-Sleep -Seconds 1
    }
}

function Test-Elevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-AppProcess { Get-Process -Name PowerLedger -ErrorAction SilentlyContinue | Where-Object Path -eq $AppExe }

function Get-Presence([string]$Path) { '{0}: {1}' -f $Path, $(if (Test-Path $Path) { 'present' } else { 'absent' }) }

function Start-Program([string]$Exe, [string[]]$Arguments) {
    $process = Start-Process $Exe -ArgumentList $Arguments -PassThru
    $null = $process.Handle   # keeps the exit code readable after the process ends
    $process
}

# Not Start-Process -Wait: that also waits for whatever setup leaves running, such as the App.
function Wait-Exit($Process, [int]$Seconds = 900) {
    if (-not $Process.WaitForExit($Seconds * 1000)) { throw "$($Process.ProcessName) is still running after $Seconds s." }
    $Process.ExitCode
}

function Get-StepLog([string]$Kind) { Join-Path $Results "$CurrentStep-$Kind.log" }

# The log a step's setup or uninstall is to write; one an earlier run left is kept under its time.
function New-StepLog([string]$Kind) {
    $log = Get-StepLog $Kind
    if (Test-Path $log) { Move-Item -LiteralPath $log -Destination ($log -replace '\.log$', ('-{0:yyyyMMdd-HHmmss}.log' -f (Get-Item $log).LastWriteTime)) -Force }
    $log
}

function Start-Setup([string]$Exe, [string[]]$Arguments = @()) {
    if (-not (Test-Path $Exe)) { throw "$Exe is missing; build it with installer\build.ps1 -TestVariants." }
    Start-Program $Exe (@($Arguments) + "/LOG=`"$(New-StepLog 'setup')`"")
}

function Invoke-Setup([string]$Exe, [string[]]$Arguments = @()) { Wait-Exit (Start-Setup $Exe $Arguments) }

function Start-Uninstall([string[]]$Arguments = @()) {
    $command = (Get-ItemProperty $UninstallKey -ErrorAction SilentlyContinue).UninstallString
    if (-not $command) { throw 'PowerLedger is not installed: it has no uninstall entry.' }
    # The entry is the quoted uninstaller, then /LOG since the script sets UninstallLogging.
    if ($command -notmatch '^\s*"([^"]+)"') { throw "Can't find the uninstaller in its entry: $command" }
    Start-Program $Matches[1] (@($Arguments) + "/LOG=`"$(New-StepLog 'uninstall')`"")
}

# unins000.exe exits with 0 as soon as its copy in %TEMP% (_unins*.tmp) takes over, before anything is removed.
function Wait-Uninstaller([int]$Seconds = 180) {
    if (-not (Wait-Until { -not (Get-Process -Name '_unins*' -ErrorAction SilentlyContinue) } -Seconds $Seconds)) {
        throw "The uninstaller is still running after $Seconds s."
    }
}

function Invoke-Uninstall([string[]]$Arguments = @()) {
    $code = Wait-Exit (Start-Uninstall $Arguments)
    Wait-Uninstaller
    $code
}

# The version in an installer's name, PowerLedger-<version>-...exe, which its uninstall entry shows.
function Get-SetupVersion([string]$Exe) {
    if ((Split-Path $Exe -Leaf) -notmatch '^PowerLedger-(\d+(?:\.\d+)+)-') { throw "Can't tell the version of $Exe from its name." }
    $Matches[1]
}

# ---- What is installed ----

function Get-ServiceState { $service = Get-Service $ServiceName -ErrorAction SilentlyContinue; if ($service) { "$($service.Status)" } else { 'absent' } }

function Get-ServiceProcessId { (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").ProcessId }

function Wait-ServiceRunning([int]$Seconds = 30) { Wait-Until { (Get-ServiceState) -eq 'Running' } -Seconds $Seconds }

# 1060 means there is no such service.
function Get-ScQueryCode {
    $null = & sc.exe query $ServiceName
    $LASTEXITCODE
}

function Get-ServiceEvent([int]$Id, [datetime]$Since) {
    Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; Id = $Id; StartTime = $Since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Properties.Value -contains $ServiceName }
}

# The run began at its Preflight, when it had one; otherwise when this invocation did.
function Get-RunStart { if (Test-Path $PreflightMarker) { (Get-Item $PreflightMarker).LastWriteTime } else { $Started } }

function Get-RunValue { (Get-ItemProperty $RunKey -ErrorAction SilentlyContinue).PowerLedger }

function Get-ServiceLog { Get-ChildItem (Join-Path $DataDir 'logs') -Filter 'service-*.log' -File -ErrorAction SilentlyContinue }

function Get-SetAside { Get-ChildItem $DataDir -File -ErrorAction SilentlyContinue | Where-Object Name -match '^power\.(untrusted|corrupt)-' }

# The machine a program is built for, from its PE header: 0x8664 for x64, 0xAA64 for Arm64.
function Get-PeMachine([string]$Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        $reader.BaseStream.Position = 0x3C
        $reader.BaseStream.Position = $reader.ReadInt32()   # where the PE header starts
        if ($reader.ReadUInt32() -ne 0x4550) { throw "$Path has no PE header." }   # 'PE' and two zero bytes
        $reader.ReadUInt16()
    }
    finally { $reader.Dispose() }
}

# The service's status from its pipe, asking again while it is starting or not yet listening.
function Get-PipeStatus([int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        try { $reply = & $PipeScript | ConvertFrom-Json }
        catch { $reply = [pscustomobject]@{ type = 'error'; message = $_.Exception.Message } }
        if ($reply.type -eq 'status') { return $reply.status }
        if ((Get-Date) -ge $deadline) { throw "No status from the service's pipe within $Seconds s: $($reply.message)" }
        Start-Sleep -Seconds 1
    }
}

# Runs as a job, in a process of its own, so the SQLite files it loads from the program folder are not held open when
# setup comes to replace or remove them.
$HistoryQuery = {
    param([string]$AppDir, [string]$Database)
    try {
        foreach ($assembly in 'SQLitePCLRaw.core', 'SQLitePCLRaw.provider.e_sqlite3', 'SQLitePCLRaw.batteries_v2', 'Microsoft.Data.Sqlite') {
            Add-Type -Path (Join-Path $AppDir "$assembly.dll")
        }
        [SQLitePCL.Batteries_V2]::Init()
        $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$Database;Mode=ReadOnly")
        try {
            $connection.Open()
            $command = $connection.CreateCommand()
            $command.CommandText = 'SELECT MIN(ts_ms), COUNT(*) FROM samples_raw'
            $reader = $command.ExecuteReader()
            $null = $reader.Read()
            [pscustomobject]@{ EarliestMs = $(if ($reader.IsDBNull(0)) { $null } else { $reader.GetInt64(0) }); Rows = $reader.GetInt64(1) }
        }
        finally {
            $connection.Dispose()
        }
    }
    catch {
        [pscustomobject]@{ Problem = $_.Exception.Message }
    }
}

# The earliest reading's time (Unix ms) and how many readings there are, or $null when they can't be read here; the
# checks then go by the database file instead.
function Get-History {
    if (-not (Test-Path (Join-Path $AppDir 'Microsoft.Data.Sqlite.dll')) -or -not (Test-Path $Database)) { return $null }
    try { $answer = Start-Job -ScriptBlock $HistoryQuery -ArgumentList $AppDir, $Database | Receive-Job -Wait -AutoRemoveJob }
    catch { $answer = [pscustomobject]@{ Problem = $_.Exception.Message } }
    if ($null -eq $answer.Rows) {
        Write-Host "  the history can't be read here: $($answer.Problem)" -ForegroundColor Yellow
        return $null
    }
    [pscustomobject]@{ EarliestMs = $answer.EarliestMs; Rows = $answer.Rows }
}

function Format-History($History) {
    if (-not $History) { return 'unreadable' }
    $earliest = if ($null -eq $History.EarliestMs) { 'none' } else { [DateTimeOffset]::FromUnixTimeMilliseconds($History.EarliestMs).LocalDateTime.ToString('yyyy-MM-dd HH:mm:ss') }
    "$($History.Rows) readings, the earliest $earliest"
}

# Readings recorded before a step must all be there after it: the same earliest one and no fewer, none set aside.
function Confirm-HistoryKept([string]$StepName, $Before) {
    $after = Get-History
    if ($Before -and $after) {
        Check $StepName 'earliest reading kept' { Assert ($after.EarliestMs -eq $Before.EarliestMs) "$(Format-History $Before) -> $(Format-History $after)" }
        Check $StepName 'no readings lost' { Assert ($after.Rows -ge $Before.Rows) "$($Before.Rows) -> $($after.Rows) readings" }
    }
    else {
        Check $StepName 'database kept (history unreadable, so by its file)' { Assert (Test-Path $Database) (Get-Presence $Database) }
    }
    Check $StepName 'nothing set aside as untrusted or corrupt' { $aside = @(Get-SetAside); Assert ($aside.Count -eq 0) "set aside: $(if ($aside) { $aside.Name -join ', ' } else { 'none' })" }
}

# Asks the App to exit through the event the installer also uses; stops it if that doesn't do it.
function Stop-App {
    if (-not (Get-AppProcess)) { return 'the App was not running' }
    $exit = $null
    try { $signalled = [Threading.EventWaitHandle]::TryOpenExisting('Local\PowerLedger.App.Exit', [ref]$exit) -and $exit.Set() }
    catch { $signalled = $false }   # refused: fall back to stopping it
    finally { if ($exit) { $exit.Dispose() } }
    if ($signalled -and (Wait-Until { -not (Get-AppProcess) } -Seconds 15)) { return 'the App closed on its exit event' }
    Get-AppProcess | Stop-Process -Force
    'the App was stopped with Stop-Process'
}

$TokenSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class PowerLedgerTestToken
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int size, out int returned);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The token's TokenElevationType (1 default, 2 full, 3 limited) and TokenElevation (non-zero when elevated).</summary>
    public static int[] Elevation(int processId)
    {
        IntPtr process = OpenProcess(0x1000, false, processId);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) throw new Win32Exception();
        try
        {
            IntPtr token;
            if (!OpenProcessToken(process, 0x0008, out token)) throw new Win32Exception();   // TOKEN_QUERY
            try
            {
                int type, elevated, size;
                if (!GetTokenInformation(token, 18, out type, 4, out size) || !GetTokenInformation(token, 20, out elevated, 4, out size))
                    throw new Win32Exception();
                return new[] { type, elevated };
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}
'@

function Get-Elevation([int]$ProcessId) {
    if (-not ('PowerLedgerTestToken' -as [type])) { Add-Type -TypeDefinition $TokenSource }
    $type, $elevated = [PowerLedgerTestToken]::Elevation($ProcessId)
    [pscustomobject]@{ Type = $type; Elevated = $elevated -ne 0 }
}

# ---- Windows, for the interactive steps ----

function Initialize-Automation { Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes }

function Get-TopWindows {
    [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
}

# The message boxes of processes named like $Process: top-level ones, and those that show as their windows' children.
function Get-Dialogs([string[]]$Process) {
    $ids = @((Get-Process -Name $Process -ErrorAction SilentlyContinue).Id)
    $isDialog = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
    foreach ($window in Get-TopWindows) {
        if ($window.Current.ProcessId -notin $ids) { continue }
        if ($window.Current.ClassName -eq '#32770') { $window }
        $window.FindAll([Windows.Automation.TreeScope]::Children, $isDialog)
    }
}

# A window's title and every piece of text on show in it.
function Get-WindowText($Window) {
    $names = foreach ($element in $Window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        if ($element.Current.Name -and -not $element.Current.IsOffscreen) { $element.Current.Name }
    }
    '[{0}] {1}' -f $Window.Current.Name, (($names | Select-Object -Unique) -join ' | ')
}

# Logs a window's text the first time it is seen.
function Write-Seen([string]$Text) {
    if (-not $Seen.Add($Text)) { return }
    Write-Host "  saw $Text" -ForegroundColor DarkCyan
    Add-Content -LiteralPath (Join-Path $Results 'dialogs.log') -Value "$(Get-Date -Format o) $CurrentStep $Text" -Encoding utf8
}

# An enabled button on show, by its caption without the '&', or else by a message box's control id.
function Find-Button($Window, [string]$Label) {
    $isButton = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    $buttons = @($Window.FindAll([Windows.Automation.TreeScope]::Descendants, $isButton) | Where-Object { $_.Current.IsEnabled -and -not $_.Current.IsOffscreen })
    $named = @($buttons | Where-Object { $_.Current.Name.Replace('&', '') -eq $Label })
    if (-not $named) { $named = @($buttons | Where-Object { $_.Current.AutomationId -in $MessageBoxIds[$Label] }) }
    $named | Select-Object -First 1
}

# Invoke blocks while setup works or a message box is up, so it runs on a thread of its own that nothing waits for.
function Press($Window, [string]$Label) {
    $button = Find-Button $Window $Label
    if (-not $button) { throw "No '$Label' button on $(Get-WindowText $Window)" }
    $null = Start-ThreadJob -Name 'PowerLedgerTest.Press' -ArgumentList $button -ScriptBlock {
        param($Button)
        $Button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    "pressed $Label"
}

function Find-Wizard {
    Get-TopWindows | Where-Object {
        try {
            $_.Current.ClassName -eq 'TWizardForm' -and $_.Current.Name -like 'Setup - PowerLedger*' -and
            (Get-Process -Id $_.Current.ProcessId -ErrorAction SilentlyContinue).ProcessName -like $SetupProcess
        }
        catch { $false }   # the window closed while being looked at
    } | Select-Object -First 1
}

function Wait-Wizard([int]$Seconds) { if (Wait-Until { Find-Wizard } -Seconds $Seconds) { Find-Wizard } }

# Waits for one of $Labels on the wizard and returns the one found. A message box from setup meanwhile is a failure.
function Wait-WizardButton($Wizard, [string[]]$Labels, [int]$Seconds) {
    $setupId = $Wizard.Current.ProcessId
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $setupId -ErrorAction SilentlyContinue)) { throw "Setup ended before showing $($Labels -join ' or ')." }
        $messages = @(try { Get-Dialogs $SetupProcess | ForEach-Object { Get-WindowText $_ } } catch { })
        foreach ($message in $messages) { Write-Seen $message }
        if ($messages) { throw "Setup showed a message: $($messages -join ' / ')" }
        $found = try {
            Write-Seen (Get-WindowText $Wizard)
            foreach ($label in $Labels) { if (Find-Button $Wizard $label) { $label; break } }
        }
        catch { $null }   # the page was changing under us: look again
        if ($found) { return $found }
        Start-Sleep -Seconds 1
    }
    throw "No $($Labels -join ' or ') button within $Seconds s."
}

# Presses Next until the Ready page's Install shows, then Install.
function Invoke-Install($Wizard) {
    foreach ($page in 1..8) {
        $label = Wait-WizardButton $Wizard 'Install', 'Next >' -Seconds 60
        $null = Press $Wizard $label
        if ($label -eq 'Install') { return "pressed Install after $($page - 1) x Next" }
        Start-Sleep -Seconds 1   # lets the next page come up
    }
    throw 'Still no Install button after eight pages.'
}

# Waits for a message box of $Process that says $Text, logs it, answers it and waits for it to close.
function Submit-Dialog([string[]]$Process, [string]$Text, [string]$Answer, [int]$Seconds = 120) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try {
            foreach ($dialog in @(Get-Dialogs $Process)) {
                $shown = Get-WindowText $dialog
                Write-Seen $shown
                if (-not $shown.Contains($Text, [StringComparison]::OrdinalIgnoreCase)) { continue }
                $handle = $dialog.Current.NativeWindowHandle
                $pressed = Press $dialog $Answer
                if (Wait-Until { -not (@(Get-Dialogs $Process) | Where-Object { $_.Current.NativeWindowHandle -eq $handle }) } -Seconds 10) {
                    return "$pressed on $shown"
                }
            }
        }
        catch { }   # a window closed while being read, or its buttons weren't ready: look again
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "No message box saying '$Text' could be answered '$Answer' within $Seconds s."
}

function Get-NewestSetupLog([datetime]$Since) {
    $log = Get-ChildItem -Path $env:TEMP, (Join-Path $env:LOCALAPPDATA 'Temp') -Filter 'Setup Log*.txt' -File -ErrorAction SilentlyContinue |
        Where-Object LastWriteTime -ge $Since | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { throw "No Setup Log*.txt in $env:TEMP written since $Since." }
    $log
}

# ---- Steps ----

function Step-Preflight {
    Remove-Item -LiteralPath $PreflightMarker -ErrorAction SilentlyContinue
    Check Preflight 'no PowerLedger service' { $code = Get-ScQueryCode; Assert ($code -eq 1060) "sc.exe query: exit code $code" }
    Check Preflight 'no uninstall entry' { Assert (-not (Test-Path $UninstallKey)) (Get-Presence $UninstallKey) }
    Check Preflight 'no program folder' { Assert (-not (Test-Path $AppDir)) (Get-Presence $AppDir) }
    Check Preflight 'no data folder' { Assert (-not (Test-Path $DataDir)) (Get-Presence $DataDir) }
    if (Test-Path $DataDir) {
        Write-Host "  $DataDir may hold somebody's history, so the run stops here. Move it away and run again." -ForegroundColor Red
        $script:StopRun = $true
        return
    }
    Set-Content -LiteralPath $PreflightMarker "Preflight found no $DataDir at $(Get-Date -Format o); this run made whatever is there later."
}

function Step-Install {
    Check Install 'silent setup exits 0' { $code = Invoke-Setup $Setup $Silent; Assert ($code -eq 0) "exit code $code" }
    Check Install 'App and service installed' {
        $missing = @($AppExe, $ServiceExe | Where-Object { -not (Test-Path $_) })
        Assert ($missing.Count -eq 0) "missing: $(if ($missing) { $missing -join ', ' } else { 'nothing' })"
    }
    Check Install 'App and service are the build for this Windows architecture' {
        $os = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        $expected = $PeMachines["$os"]
        if (-not $expected) { throw "PowerLedger has no build for $os Windows." }
        $found = foreach ($exe in $AppExe, $ServiceExe) { [pscustomobject]@{ Name = Split-Path $exe -Leaf; Machine = Get-PeMachine $exe } }
        $wrong = @($found | Where-Object Machine -ne $expected)
        Assert ($wrong.Count -eq 0) ('{0} Windows wants 0x{1:X4}: {2}' -f $os, $expected, (($found | ForEach-Object { '{0} 0x{1:X4}' -f $_.Name, $_.Machine }) -join ', '))
    }
    Check Install 'App and service each carry the .NET runtime' {
        $runtime = foreach ($folder in $AppDir, (Split-Path $ServiceExe)) { Join-Path $folder 'hostfxr.dll'; Join-Path $folder 'coreclr.dll' }
        $missing = @($runtime | Where-Object { -not (Test-Path $_) })
        Assert ($missing.Count -eq 0) "missing: $(if ($missing) { $missing -join ', ' } else { 'nothing' })"
    }
    Check Install 'Start menu shortcut opens the App' {
        if (-not (Test-Path $Shortcut)) { throw "$Shortcut is missing." }
        $target = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut).TargetPath
        Assert ($target -eq $AppExe) "target $target"
    }
    Check Install 'uninstall entry' {
        $entry = Get-ItemProperty $UninstallKey
        $expected = Get-SetupVersion $Setup
        Assert ($entry.DisplayName -eq 'PowerLedger' -and $entry.DisplayVersion -eq $expected -and $entry.Publisher -eq 'PowerLedger') `
            "$($entry.DisplayName) $($entry.DisplayVersion) from $($entry.Publisher)"
    }
}

function Step-Service {
    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    Check Service 'registered' { Assert $service $(if ($service) { $service.DisplayName } else { 'no such service' }) }
    if (-not $service) { return }
    Check Service 'image path is the quoted service program' { Assert ($service.PathName -eq "`"$ServiceExe`"") $service.PathName }
    Check Service 'starts automatically as LocalSystem' {
        Assert ($service.StartMode -eq 'Auto' -and $service.StartName -eq 'LocalSystem') "$($service.StartMode), $($service.StartName)"
    }
    Check Service 'has a description' { Assert $service.Description "'$($service.Description)'" }
    Check Service 'running within 30 s' { Assert (Wait-ServiceRunning 30) (Get-ServiceState) }
    Check Service 'restarts three times, 5 s apart, counted over a day' {
        $failure = (& sc.exe qfailure $ServiceName) -join "`n"
        $restarts = [regex]::Matches($failure, 'RESTART -- Delay = 5000 milliseconds').Count
        Assert ($failure -match 'RESET_PERIOD \(in seconds\)\s*:\s*86400' -and $restarts -eq 3) `
            (($failure -split "`n" | Where-Object { $_ -match 'RESET_PERIOD|RESTART' }).Trim() -join ' / ')
    }
    Check Service 'failure actions also after an error exit' {
        $flag = (& sc.exe qfailureflag $ServiceName) -join "`n"
        Assert ($flag -match 'FAILURE_ACTIONS_ON_NONCRASH_FAILURES\s*:\s*TRUE') (($flag -split "`n" | Where-Object { $_ -match 'FAILURE_ACTIONS' }).Trim() -join ' / ')
    }
    Check Service 'install event 7045' {
        $since = Get-RunStart
        $installed = @(Get-ServiceEvent 7045 $since)
        Assert $installed.Count "$($installed.Count) since $since"
    }
}

function Step-Data {
    Check Data 'folder made' { Assert (Wait-Until { Test-Path $DataDir } -Seconds 30) (Get-Presence $DataDir) }
    if (-not (Test-Path $DataDir)) { return }
    $acl = Get-Acl -LiteralPath $DataDir
    Check Data 'owned by SYSTEM or Administrators' { Assert ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -in 'S-1-5-18', 'S-1-5-32-544') $acl.Owner }
    Check Data 'inherits nothing' { Assert $acl.AreAccessRulesProtected "rules protected: $($acl.AreAccessRulesProtected)" }
    Check Data 'SYSTEM and Administrators full control, Users read, nobody else' {
        $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
                '{0} {1} {2} ({3}; {4})' -f $_.IdentityReference, $_.AccessControlType, $_.FileSystemRights, $_.InheritanceFlags, $_.PropagationFlags
            })
        Assert (-not (Compare-Object $ExpectedRules $rules)) ($rules -join '; ')
    }
    Check Data 'database made' { Assert (Test-Path $Database) (Get-Presence $Database) }
    Check Data 'a service log within 60 s' { Assert (Wait-Until { Get-ServiceLog } -Seconds 60) "logs: $(@(Get-ServiceLog).Name -join ', ')" }
    Check Data 'status: ticking, this version, no database notice' {
        $status = Get-PipeStatus
        Assert ($status.ticks -ge 1 -and $status.version -like "$version*" -and $null -eq $status.databaseNotice) `
            "ticks $($status.ticks), version $($status.version), databaseNotice $($status.databaseNotice ?? 'null')"
    }
    if (Get-History) {
        Check Data 'readings stored within 90 s' { Assert (Wait-Until { (Get-History).Rows -gt 0 } -Seconds 90) (Format-History (Get-History)) }
    }
    else {
        Skip Data 'readings stored within 90 s' 'SQLite could not be loaded from the program folder'
    }
    Check Data 'event log source registered' { Assert (Test-Path $EventSourceKey) (Get-Presence $EventSourceKey) }
}

function Step-Recovery {
    $first = Get-ServiceProcessId
    $killed = Get-Date
    Check Recovery 'service process killed' {
        if (-not $first) { throw 'The service is not running.' }
        Stop-Process -Id $first -Force
        "process $first"
    }
    Check Recovery 'running again, in a new process, within 30 s' {
        Assert (Wait-Until { (Get-ServiceState) -eq 'Running' -and (Get-ServiceProcessId) -notin 0, $first } -Seconds 30) `
            "process $first -> $(Get-ServiceProcessId), $(Get-ServiceState)"
    }
    Check Recovery 'crash event 7031' { $crashes = @(Get-ServiceEvent 7031 $killed.AddSeconds(-1)); Assert $crashes.Count "$($crashes.Count) since the kill" }
}

# A clean stop keeps the write-ahead log and shared memory: the App reads with the Users group's read-only access to the
# folder, so it could not open the history again if SQLite deleted them (spec §7).
function Step-ServiceStop {
    if (Get-AppProcess) {
        Skip ServiceStop 'write-ahead log and shared memory kept after a clean stop' 'the App holds the database open, which keeps them anyway'
        return
    }
    Check ServiceStop 'stops cleanly' {
        Stop-Service $ServiceName
        Assert (Wait-Until { (Get-ServiceState) -eq 'Stopped' } -Seconds 60) (Get-ServiceState)
    }
    Check ServiceStop 'write-ahead log and shared memory kept' {
        $missing = @("$Database-wal", "$Database-shm" | Where-Object { -not (Test-Path $_) })
        Assert ($missing.Count -eq 0) "missing: $(if ($missing) { $missing -join ', ' } else { 'nothing' })"
    }
    Check ServiceStop 'starts again' { Start-Service $ServiceName; Assert (Wait-ServiceRunning 30) (Get-ServiceState) }
}

function Step-Upgrade {
    $appRan = [bool](Get-AppProcess)
    $before = Get-History
    Write-Host "  before: the App $(if ($appRan) { 'running' } else { 'not running' }); $(Format-History $before)"
    Check Upgrade 'silent upgrade exits 0' { $code = Invoke-Setup $Upgrade $Silent; Assert ($code -eq 0) "exit code $code" }
    if ($appRan) {
        Check Upgrade 'the running App was closed' { Assert (Wait-Until { -not (Get-AppProcess) } -Seconds 10) "App processes: $(@(Get-AppProcess).Count)" }
    }
    Check Upgrade 'uninstall entry shows the new version' {
        $shown = (Get-ItemProperty $UninstallKey).DisplayVersion
        Assert ($shown -eq (Get-SetupVersion $Upgrade)) "DisplayVersion $shown"
    }
    Check Upgrade 'service running' { Assert (Wait-ServiceRunning 30) (Get-ServiceState) }
    Confirm-HistoryKept Upgrade $before
}

function Step-UninstallKeep {
    $appRan = [bool](Get-AppProcess)
    ConvertTo-Json -InputObject (Get-History) | Set-Content -LiteralPath $SavedHistory   # for Reinstall to compare with
    Check UninstallKeep 'silent uninstall exits 0' { $code = Invoke-Uninstall $Silent; Assert ($code -eq 0) "exit code $code" }
    if ($appRan) {
        Check UninstallKeep 'the running App was closed' { Assert (Wait-Until { -not (Get-AppProcess) } -Seconds 10) "App processes: $(@(Get-AppProcess).Count)" }
    }
    Check UninstallKeep 'service removed' { Assert (Wait-Until { (Get-ScQueryCode) -eq 1060 } -Seconds 30) "sc.exe query: exit code $(Get-ScQueryCode)" }
    Check UninstallKeep 'program folder removed' { Assert (-not (Test-Path $AppDir)) (Get-Presence $AppDir) }
    Check UninstallKeep 'Start menu shortcut removed' { Assert (-not (Test-Path $Shortcut)) (Get-Presence $Shortcut) }
    Check UninstallKeep 'uninstall entry removed' { Assert (-not (Test-Path $UninstallKey)) (Get-Presence $UninstallKey) }
    Check UninstallKeep 'start-with-Windows entry removed' { $value = Get-RunValue; Assert ($null -eq $value) "Run\PowerLedger: $($value ?? 'absent')" }
    Check UninstallKeep 'event log source removed' { Assert (-not (Test-Path $EventSourceKey)) (Get-Presence $EventSourceKey) }
    Check UninstallKeep 'history kept' { Assert (Test-Path $Database) (Get-Presence $Database) }
}

function Step-Reinstall {
    $before = if (Test-Path $SavedHistory) { Get-Content -LiteralPath $SavedHistory -Raw | ConvertFrom-Json }
    Check Reinstall 'silent setup over the kept history exits 0' { $code = Invoke-Setup $Setup $Silent; Assert ($code -eq 0) "exit code $code" }
    Check Reinstall 'service running' { Assert (Wait-ServiceRunning 30) (Get-ServiceState) }
    Check Reinstall 'status: no database notice' { $status = Get-PipeStatus; Assert ($null -eq $status.databaseNotice) "databaseNotice $($status.databaseNotice ?? 'null')" }
    Confirm-HistoryKept Reinstall $before
}

function Step-UninstallDelete {
    Initialize-Automation
    $uninstaller = Start-Uninstall
    Check UninstallDelete 'Yes to removing PowerLedger' { Submit-Dialog $UninstallProcess 'Are you sure' 'Yes' -Seconds 60 }
    Check UninstallDelete 'No to keeping the history' { Submit-Dialog $UninstallProcess 'Keep your PowerLedger history' 'No' -Seconds 180 }
    Check UninstallDelete 'OK to "successfully removed"' {
        $answered = Submit-Dialog $UninstallProcess 'removed' 'OK' -Seconds 180   # any removal message is dismissed, but only this one passes
        Assert ($answered -like '*successfully removed*') $answered
    }
    Check UninstallDelete 'uninstaller finished' { $code = Wait-Exit $uninstaller 60; Wait-Uninstaller; Assert ($code -eq 0) "exit code $code" }
    Check UninstallDelete 'history deleted' { Assert (-not (Test-Path $DataDir)) (Get-Presence $DataDir) }
    Check UninstallDelete 'service removed' { Assert (Wait-Until { (Get-ScQueryCode) -eq 1060 } -Seconds 30) "sc.exe query: exit code $(Get-ScQueryCode)" }
}

# The installer's wizard end to end, in a setup this run starts, as somebody new to PowerLedger would install it.
# This run is elevated, so the App the wizard opens is too; DriveWizard is the step for the unelevated start.
function Step-InstallWizard {
    Initialize-Automation
    $setup = Start-Setup $Setup
    $wizard = Wait-Wizard 60
    Check InstallWizard 'wizard opens' { Assert $wizard $(if ($wizard) { $wizard.Current.Name } else { 'no Setup - PowerLedger window within 60 s' }) }
    if ($wizard) {
        Check InstallWizard 'Install pressed' { Invoke-Install $wizard }
        Check InstallWizard 'finished within 300 s, Finish pressed with Open PowerLedger ticked' {
            $null = Wait-WizardButton $wizard 'Finish' -Seconds 300
            Press $wizard 'Finish'
        }
    }
    Check InstallWizard 'setup exits 0' { $code = Wait-Exit $setup 120; Assert ($code -eq 0) "exit code $code" }
    Check InstallWizard 'setup log has no exception and no "could not"' {
        $log = Get-StepLog 'setup'
        $bad = @(Select-String -LiteralPath $log -Pattern 'Exception', 'could not' -SimpleMatch)
        Assert ($bad.Count -eq 0) "$(Split-Path $log -Leaf): $(if ($bad) { $bad.Line -join ' / ' } else { 'clean' })"
    }
    Check InstallWizard 'service running' { Assert (Wait-ServiceRunning 60) (Get-ServiceState) }
    Check InstallWizard 'the App opens within 30 s' { Assert (Wait-Until { Get-AppProcess } -Seconds 30) "App processes: $(@(Get-AppProcess).Count)" }
    Check InstallWizard 'the App the wizard opened is closed again' {
        $how = Stop-App
        Assert (Wait-Until { -not (Get-AppProcess) } -Seconds 15) $how
    }
}

# For a setup somebody else started, so it can be run unelevated and ask for elevation itself.
function Step-DriveWizard {
    Initialize-Automation
    $since = Get-Date
    Write-Host "  waiting up to $WizardTimeout s for somebody to start PowerLedger's setup"
    $wizard = Wait-Wizard $WizardTimeout
    Check DriveWizard 'a setup wizard appears' { Assert $wizard $(if ($wizard) { $wizard.Current.Name } else { "none within $WizardTimeout s" }) }
    if (-not $wizard) { return }
    Check DriveWizard 'Install pressed' { Invoke-Install $wizard }
    Check DriveWizard 'finished within 600 s, Finish pressed with Open PowerLedger ticked' {
        $null = Wait-WizardButton $wizard 'Finish' -Seconds 600
        Press $wizard 'Finish'
    }
    Check DriveWizard 'the App opens, unelevated, within 30 s' {
        if (-not (Wait-Until { Get-AppProcess } -Seconds 30)) { throw "$AppExe is not running." }
        $token = Get-Elevation (Get-AppProcess | Select-Object -First 1).Id
        Assert (-not $token.Elevated) "token elevation type $($token.Type), elevated $($token.Elevated)"
    }
    Check DriveWizard 'setup log has no exception and no "could not"' {
        $log = Get-NewestSetupLog $since
        $bad = @(Select-String -LiteralPath $log.FullName -Pattern 'Exception', 'could not' -SimpleMatch)
        Assert ($bad.Count -eq 0) "$($log.Name): $(if ($bad) { $bad.Line -join ' / ' } else { 'clean' })"
    }
    Check DriveWizard 'service running' { Assert (Wait-ServiceRunning 60) (Get-ServiceState) }
}

function Step-Cleanup {
    Check Cleanup 'App closed' { $how = Stop-App; Assert (Wait-Until { -not (Get-AppProcess) } -Seconds 10) $how }
    if (Test-Path $UninstallKey) {
        Check Cleanup 'uninstalled silently' {
            $code = Invoke-Uninstall $Silent
            Assert ($code -eq 0 -and -not (Test-Path $UninstallKey)) "exit code $code, $(Get-Presence $UninstallKey)"
        }
    }
    if (Test-Path $PreflightMarker) {
        Check Cleanup "this run's data folder deleted" {
            if (Test-Path $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
            Assert (-not (Test-Path $DataDir)) (Get-Presence $DataDir)
        }
        if (-not (Test-Path $DataDir)) { Remove-Item -LiteralPath $PreflightMarker }
    }
    else {
        Skip Cleanup 'data folder deleted' "no Preflight in $Results found it absent, so $DataDir is left alone"
    }
    Check Cleanup 'start-with-Windows entry removed' {
        Remove-ItemProperty -Path $RunKey -Name PowerLedger -ErrorAction SilentlyContinue
        $value = Get-RunValue
        Assert ($null -eq $value) "Run\PowerLedger: $($value ?? 'absent')"
    }
}

# ---- The run ----

foreach ($CurrentStep in $Step) {
    Write-Host "== $CurrentStep" -ForegroundColor Cyan
    if (-not (Test-Elevated)) {
        Write-Result $CurrentStep 'elevated' $false 'Every step needs an elevated PowerShell: run it as administrator.'
        continue
    }
    try { & "Step-$CurrentStep" }
    catch { Write-Result $CurrentStep 'ran to the end' $false $_.Exception.Message }
    if ($StopRun) { break }
}

Get-Job -Name 'PowerLedgerTest.Press' -ErrorAction SilentlyContinue | Where-Object State -ne 'Running' | Remove-Job
$Records | Format-Table step, check, ok, detail -Wrap | Out-String -Width 200 | Write-Host
$failures = @($Records | Where-Object { -not $_.ok }).Count
Write-Host ('{0} checks, {1} failed; results in {2}' -f $Records.Count, $failures, $ResultsFile)
exit $failures

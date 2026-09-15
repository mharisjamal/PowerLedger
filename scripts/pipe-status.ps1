<#
.SYNOPSIS
Asks a running PowerLedger service for its status and prints the reply.

.EXAMPLE
./scripts/pipe-status.ps1
./scripts/pipe-status.ps1 -Pipe PowerLedger.dev
#>
param(
    [string]$Pipe = 'PowerLedger.v1',
    [string]$Request = '{"type":"getStatus","id":1}'
)

$ErrorActionPreference = 'Stop'
$client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(5000)
    $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $reader = [System.IO.StreamReader]::new($client)
    $writer.Write($Request + "`n")
    $reader.ReadLine()
}
finally {
    $client.Dispose()
}

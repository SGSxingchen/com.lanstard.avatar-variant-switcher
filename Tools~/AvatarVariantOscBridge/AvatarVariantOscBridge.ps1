param(
    [Parameter(Mandatory = $true)]
    [string]$MappingPath,
    [string]$Host = "127.0.0.1",
    [int]$ListenPort = 9001,
    [int]$SendPort = 9000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-PaddedString {
    param(
        [byte[]]$Buffer,
        [ref]$Offset
    )

    $start = $Offset.Value
    while ($Offset.Value -lt $Buffer.Length -and $Buffer[$Offset.Value] -ne 0) {
        $Offset.Value++
    }

    if ($Offset.Value -ge $Buffer.Length) {
        throw "Invalid OSC string."
    }

    $value = [System.Text.Encoding]::UTF8.GetString($Buffer, $start, $Offset.Value - $start)

    while ($Offset.Value -lt $Buffer.Length -and $Buffer[$Offset.Value] -eq 0) {
        $Offset.Value++
        if (($Offset.Value % 4) -eq 0) {
            break
        }
    }

    return $value
}

function Read-Int32BigEndian {
    param(
        [byte[]]$Buffer,
        [ref]$Offset
    )

    $value = [System.BitConverter]::ToInt32([byte[]]($Buffer[$Offset.Value + 3], $Buffer[$Offset.Value + 2], $Buffer[$Offset.Value + 1], $Buffer[$Offset.Value]), 0)
    $Offset.Value += 4
    return $value
}

function Read-FloatBigEndian {
    param(
        [byte[]]$Buffer,
        [ref]$Offset
    )

    $bytes = [byte[]]($Buffer[$Offset.Value + 3], $Buffer[$Offset.Value + 2], $Buffer[$Offset.Value + 1], $Buffer[$Offset.Value])
    $Offset.Value += 4
    return [System.BitConverter]::ToSingle($bytes, 0)
}

function Read-OscMessage {
    param([byte[]]$Buffer)

    try {
        $offset = 0
        $address = Read-PaddedString -Buffer $Buffer -Offset ([ref]$offset)
        if ([string]::IsNullOrWhiteSpace($address)) {
            return $null
        }

        $typeTag = Read-PaddedString -Buffer $Buffer -Offset ([ref]$offset)
        if ([string]::IsNullOrWhiteSpace($typeTag) -or -not $typeTag.StartsWith(",")) {
            return $null
        }

        $arguments = New-Object System.Collections.Generic.List[object]
        for ($i = 1; $i -lt $typeTag.Length; $i++) {
            switch ($typeTag[$i]) {
                "i" { [void]$arguments.Add((Read-Int32BigEndian -Buffer $Buffer -Offset ([ref]$offset))) }
                "f" { [void]$arguments.Add((Read-FloatBigEndian -Buffer $Buffer -Offset ([ref]$offset))) }
                "s" { [void]$arguments.Add((Read-PaddedString -Buffer $Buffer -Offset ([ref]$offset))) }
                "T" { [void]$arguments.Add($true) }
                "F" { [void]$arguments.Add($false) }
                default { return $null }
            }
        }

        return [pscustomobject]@{
            Address   = $address
            Arguments = $arguments
        }
    }
    catch {
        return $null
    }
}

function Write-PaddedString {
    param(
        [System.IO.MemoryStream]$Stream,
        [string]$Value
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.WriteByte(0)

    while (($Stream.Length % 4) -ne 0) {
        $Stream.WriteByte(0)
    }
}

function New-OscMessage {
    param(
        [string]$Address,
        [string]$StringValue
    )

    $stream = New-Object System.IO.MemoryStream
    try {
        Write-PaddedString -Stream $stream -Value $Address
        Write-PaddedString -Stream $stream -Value ",s"
        Write-PaddedString -Stream $stream -Value $StringValue
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Load-Mapping {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Mapping file not found: $Path"
    }

    $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $mapping = $json | ConvertFrom-Json

    if ([string]::IsNullOrWhiteSpace($mapping.parameterName)) {
        throw "Mapping file is missing parameterName."
    }

    return $mapping
}

function Get-ParameterValue {
    param($Message)

    if ($Message.Arguments.Count -eq 0) {
        return $null
    }

    $arg = $Message.Arguments[0]
    if ($arg -is [int]) {
        return $arg
    }
    if ($arg -is [single] -or $arg -is [double]) {
        return [int][math]::Round([double]$arg)
    }
    if ($arg -is [bool]) {
        if ($arg) { return 1 }
        return 0
    }

    return $null
}

$MappingPath = [System.IO.Path]::GetFullPath($MappingPath)
$mapping = Load-Mapping -Path $MappingPath
$mappingTimestamp = (Get-Item -LiteralPath $MappingPath).LastWriteTimeUtc
$parameterAddress = "/avatar/parameters/$($mapping.parameterName)"
$currentAvatarId = $null
$lastObservedValue = $null

$listener = New-Object System.Net.Sockets.UdpClient($ListenPort)
$sender = New-Object System.Net.Sockets.UdpClient
$targetEndpoint = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse($Host), $SendPort)
$remoteEndpoint = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)

Write-Host "Mapping: $MappingPath"
Write-Host "Listening: 0.0.0.0:$ListenPort"
Write-Host "Sending: ${Host}:$SendPort"
Write-Host "Parameter: $($mapping.parameterName)"
Write-Host "Press Ctrl+C to stop."

try {
    while ($true) {
        $currentTimestamp = (Get-Item -LiteralPath $MappingPath).LastWriteTimeUtc
        if ($currentTimestamp -ne $mappingTimestamp) {
            $mapping = Load-Mapping -Path $MappingPath
            $mappingTimestamp = $currentTimestamp
            $parameterAddress = "/avatar/parameters/$($mapping.parameterName)"
            Write-Host "Reloaded mapping file."
        }

        $bytes = $listener.Receive([ref]$remoteEndpoint)
        $message = Read-OscMessage -Buffer $bytes
        if ($null -eq $message) {
            continue
        }

        if ($message.Address -eq "/avatar/change") {
            if ($message.Arguments.Count -gt 0 -and $message.Arguments[0] -is [string]) {
                $currentAvatarId = $message.Arguments[0]
                Write-Host "Current avatar: $currentAvatarId"
            }
            continue
        }

        if ($message.Address -ne $parameterAddress) {
            continue
        }

        $value = Get-ParameterValue -Message $message
        if ($null -eq $value) {
            continue
        }

        if ($lastObservedValue -eq $value) {
            continue
        }

        $lastObservedValue = $value
        $entry = $mapping.variants | Where-Object { $_.paramValue -eq $value } | Select-Object -First 1

        if ($null -eq $entry) {
            Write-Host "No avatar mapped for value $value."
            continue
        }

        if ([string]::IsNullOrWhiteSpace($entry.blueprintId)) {
            Write-Host "Value $value is mapped, but blueprintId is empty."
            continue
        }

        if ($entry.blueprintId -eq $currentAvatarId) {
            Write-Host "Value $value already matches current avatar."
            continue
        }

        $packet = New-OscMessage -Address "/avatar/change" -StringValue $entry.blueprintId
        [void]$sender.Send($packet, $packet.Length, $targetEndpoint)
        $currentAvatarId = $entry.blueprintId
        Write-Host "Switched avatar for value $value -> $($entry.displayName) ($($entry.blueprintId))"
    }
}
finally {
    $listener.Dispose()
    $sender.Dispose()
}

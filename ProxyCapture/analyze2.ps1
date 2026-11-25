# =====================================================================
#  PHAN TICH LOG CAPTURE - GIAI NEN GZIP - TIM CHUOI PREFIX  (v2)
# =====================================================================
$ErrorActionPreference = "Stop"
$capDir  = Join-Path $PSScriptRoot "bin\Debug\net8.0\capture"
$outFile = Join-Path $capDir "analyze-result.txt"

function Get-HexFromLog([string]$logPath) {
    $c2s = New-Object System.Collections.Generic.List[byte]
    $s2c = New-Object System.Collections.Generic.List[byte]
    $dir = $null
    foreach ($line in (Get-Content $logPath -Encoding UTF8)) {
        if ($line -match '^\[[\d:.]+\] (C->S|S->C) \(') { $dir = $Matches[1]; continue }
        if ($line -match '^\s*HEX:\s*([0-9A-F]+)$') {
            if ($null -eq $dir) { continue }
            $hex = $Matches[1]
            for ($i = 0; $i + 1 -lt $hex.Length; $i += 2) {
                $b = [Convert]::ToByte($hex.Substring($i, 2), 16)
                if ($dir -eq "C->S") { [void]$c2s.Add($b) } else { [void]$s2c.Add($b) }
            }
        }
    }
    return @{ C = [byte[]]$c2s.ToArray(); S = [byte[]]$s2c.ToArray() }
}

function Get-BinStreams([string]$cPath, [string]$sPath) {
    $c = [byte[]](Get-Content $cPath -Encoding Byte -ErrorAction SilentlyContinue)
    $s = [byte[]](Get-Content $sPath -Encoding Byte -ErrorAction SilentlyContinue)
    return @{ C = $c; S = $s }
}

function Expand-GzipAt([byte[]]$data, [int]$offset) {
    # Tra ve chuoi giai nen (co the partial). $null neu khong giai duoc.
    $out = New-Object System.IO.MemoryStream
    try {
        # cat slice de tranh ambiguous constructor
        $slice = New-Object byte[] ($data.Length - $offset)
        [Array]::Copy($data, $offset, $slice, 0, $slice.Length)
        $ms = New-Object System.IO.MemoryStream(,[byte[]]$slice)
        $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
        $buf = New-Object byte[] 65536
        while ($true) {
            try { $n = $gz.Read($buf, 0, $buf.Length) }
            catch { break }   # luong bi cat (hex log chi 1024 bytes) -> giu partial
            if ($n -le 0) { break }
            [void]$out.Write($buf, 0, $n)
        }
        $gz.Dispose(); $ms.Dispose()
    } catch { }
    $raw = $out.ToArray()
    if ($raw.Length -lt 5) { return $null }
    try {
        return [System.Text.Encoding]::UTF8.GetString($raw)
    } catch {
        return $null
    }
}

$report = New-Object System.Text.StringBuilder
[void]$report.AppendLine("===== VOS3000 ANALYZE v2 - $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) =====")

$allStreamTexts = @()

foreach ($log in (Get-ChildItem $capDir -Filter "session_*.log" -ErrorAction SilentlyContinue)) {
    [void]$report.AppendLine("")
    [void]$report.AppendLine("########## $($log.Name) ##########")
    # uu tien file .bin (day du 100%), fallback ve hex log
    $base = $log.FullName -replace '_\d+\.log$', ''  # bo thoi gian
    $baseDir = Split-Path $log.FullName
    $name = [IO.Path]::GetFileNameWithoutExtension($log.Name)
    $sessId = ($name -split '_')[1]
    $cPath = Join-Path $baseDir "s${sessId}_c2s.bin"
    $sPath = Join-Path $baseDir "s${sessId}_s2c.bin"
    if ((Test-Path $cPath) -and (Test-Path $sPath)) {
        $streams = Get-BinStreams -cPath $cPath -sPath $sPath
    } else {
        $streams = Get-HexFromLog -logPath $log.FullName
    }
    foreach ($k in @("C","S")) {
        $label = if ($k -eq "C") { "CLIENT->SERVER" } else { "SERVER->CLIENT" }
        $data  = $streams[$k]
        [void]$report.AppendLine("")
        [void]$report.AppendLine("== $label : $($data.Length) bytes ==")
        if ($data.Length -lt 100) { continue }

        # scan den tu cuoi - 2
        $idx = 0
        $count = 0
        while ($true) {
            $idx = [Array]::IndexOf($data, [byte]0x1F, $idx)
            if ($idx -lt 0 -or $idx + 2 -ge $data.Length) { break }
            if ($data[$idx+1] -eq 0x8B -and $data[$idx+2] -eq 0x08) {
                $count++
                $txt = Expand-GzipAt -data $data -offset $idx
                if ($txt) {
                    [void]$report.AppendLine("")
                    [void]$report.AppendLine("--- gzip #$count @ byte $idx, STREAM $($txt.Length) ky tu ---")
                    $show = $txt
                    if ($show.Length -gt 15000) { $show = $show.Substring(0,15000) + "(...con lai giai nen duoc nhung cat de doc)" }
                    [void]$report.AppendLine($show)
                    $allStreamTexts += $txt
                }
                $idx += 3
            } else {
                $idx++
            }
        }
    }
}

# Tim alberts: gio stream chua keywords
$report.AppendLine("")
[void]$report.AppendLine("===== KET QUA TIM KIEM PREFIX =====")
$found = 0
for ($i = 0; $i -lt $allStreamTexts.Count; $i++) {
    $t = $allStreamTexts[$i]
    foreach ($kw in @("0999","0901","0912","gatewaymapping","GatewayMappingSetting","calloutcallerprefix","calloutcaller")) {
        if ($t.Contains($kw)) {
            $found++
            [void]$report.AppendLine("")
            [void]$report.AppendLine("### STREAM $i CHUA '$kw' ###")
            # in tung doan xung quan vi tri keyword
            foreach ($line in ($t -split "`n" | ForEach-Object { $_ })) {
                if ($line -match '0999|0901|0912|apparent|prefix') {
                    [void]$report.AppendLine("    >>> $line")
                }
            }
            break
        }
    }
}
[void]$report.AppendLine("")
[void]$report.AppendLine("Tong stream tim thay keyword: $found")

[System.IO.File]::WriteAllText($outFile, $report.ToString(), [System.Text.Encoding]::UTF8)
Write-Output ">> $outFile ($((Get-Item $outFile).Length) bytes)"
Write-Output "So stream giai nen duoc: $($allStreamTexts.Count)"
Write-Output "So stream chua keyword: $found"

param([string]$LogDir = "")

# Decoder: doc log capture cua proxy, hien thi goi tin de doc
if ($LogDir -eq "") {
    $LogDir = Join-Path $PSScriptRoot "bin\Debug\net8.0\capture"
}

$files = Get-ChildItem $LogDir -Filter "*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
if (-not $files) { Write-Output "Khong co log trong $LogDir"; exit 1 }

Write-Output "=================================================="
Write-Output " VOS3000 CAPTURE ANALYSIS"
Write-Output "=================================================="
Write-Output "Tim thay $($files.Count) file log"
Write-Output ""

foreach ($f in $files) {
    Write-Output ("=" * 70)
    Write-Output "FILE: $($f.Name)  ($($f.Length) bytes)"
    Write-Output ("=" * 70)

    $lines = Get-Content $f.FullName
    $sizes = @{ "C->S" = 0; "S->C" = 0 }
    $counts = @{ "C->S" = 0; "S->C" = 0 }

    foreach ($line in $lines) {
        if ($line -match '^\[.*?\]\s+(C->S|S->C)\s+\((\d+) bytes') {
            $dir = $Matches[1]; $n = [int]$Matches[2]
            $sizes[$dir] += $n; $counts[$dir]++
        }
    }

    Write-Output ""
    Write-Output "TONG KET:"
    Write-Output ("  Client -> Server : {0} goi, {1} bytes" -f $counts["C->S"], $sizes["C->S"])
    Write-Output ("  Server -> Client : {0} goi, {1} bytes" -f $counts["S->C"], $sizes["S->C"])
    Write-Output ""

    if ($sizes["C->S"] -eq 0) {
        Write-Output "  (Khong co du lieu Client->Server - phien chi ket noi roi dong)"
        Write-Output ""
        continue
    }

    Write-Output "CHI TIET (cac goi co du lieu):"
    Write-Output ""

    $i = 0
    foreach ($line in $lines) {
        if ($line -match '^\[(.*?)\]\s+(C->S|S->C)\s+\((\d+) bytes') {
            $i++
            $time = $Matches[1]; $dir = $Matches[2]; $n = $Matches[3]
            if ([int]$n -lt 4) { continue }
            Write-Output ("  #{0:D3} [{1}] {2}  {3} bytes" -f $i, $time, $dir, $n)
        }
    }
    Write-Output ""
}

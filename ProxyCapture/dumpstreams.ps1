# =====================================================================
#  DUMP MOI STREAM GIAI NEN RA FILE RIENG DE PHAN TICH KY
# =====================================================================
$ErrorActionPreference = "Stop"
$capDir  = Join-Path $PSScriptRoot "bin\Debug\net8.0\capture"
$outDir  = Join-Path $capDir "streams"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

function Expand-GzipAt([byte[]]$data, [int]$offset) {
    $out = New-Object System.IO.MemoryStream
    try {
        $slice = New-Object byte[] ($data.Length - $offset)
        [Array]::Copy($data, $offset, $slice, 0, $slice.Length)
        $ms = New-Object System.IO.MemoryStream(,[byte[]]$slice)
        $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
        $buf = New-Object byte[] 65536
        while ($true) {
            try { $n = $gz.Read($buf, 0, $buf.Length) }
            catch { break }
            if ($n -le 0) { break }
            [void]$out.Write($buf, 0, $n)
        }
        $gz.Dispose(); $ms.Dispose()
    } catch { }
    return $out.ToArray()
}

foreach ($bin in (Get-ChildItem $capDir -Filter "s*_*.bin" -ErrorAction SilentlyContinue)) {
    $data = [byte[]](Get-Content $bin.FullName -Encoding Byte)
    $dirLabel = if ($bin.Name -like "*_c2s.bin") { "C2S" } else { "S2C" }
    $sess = $bin.Name.Substring(1,2)
    Write-Output "$($bin.Name) : $($data.Length) bytes"
    $idx = 0; $count = 0
    while ($true) {
        $idx = [Array]::IndexOf($data, [byte]0x1F, $idx)
        if ($idx -lt 0 -or $idx + 2 -ge $data.Length) { break }
        if ($data[$idx+1] -eq 0x8B -and $data[$idx+2] -eq 0x08) {
            $count++
            $raw = Expand-GzipAt -data $data -offset $idx
            if ($raw -and $raw.Length -gt 10) {
                $txt = [System.Text.Encoding]::UTF8.GetString($raw)
                # tim ten class de dat ten file
                $cls = ""
                if ($txt -match 'com\.kunshi\.interfacef\.vos2000\.(\w+)') { $cls = $Matches[1] }
                elseif ($txt -match 'com\.kunshi\.interfacef\.common\.(\w+)') { $cls = $Matches[1] }
                elseif ($txt -match 'java\.util\.(\w+)') { $cls = "Java_$($Matches[1])" }
                $outFile = Join-Path $outDir ("s{0}_{1}_{2:D2}_off{3}_{4}.txt" -f $sess, $dirLabel, $count, $idx, $cls)
                [System.IO.File]::WriteAllText($outFile, $txt, [System.Text.Encoding]::UTF8)
            }
            $idx += 3
        } else { $idx++ }
    }
}

Write-Output ""
Write-Output "=== KET QUA DUMP VAO $outDir ==="
Get-ChildItem $outDir -Filter "*.txt" | Sort-Object Name | ForEach-Object {
    $has = ""
    $c = Get-Content $_.FullName -Raw -Encoding UTF8
    if ($c -match "0999|0901|0912|GatewayMapping") { $has = "  <<<< CO PREFIX/MAPPING" }
    "{0}  ({1} bytes){2}" -f $_.Name, $_.Length, $has
}

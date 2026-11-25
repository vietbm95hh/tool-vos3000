# Neu tu luong decompressed file .txt theo ca Java serialization strings (TC_STRING)
$dir = "C:\Users\PC\Documents\Default Project\Vos3000AgentCSharp\ProxyCapture\bin\Debug\net8.0\capture\streams"
$targets = @("s01_C2S_44_off12152_InfoGatewayMapping.txt","s01_S2C_17_off10478_InfoGatewayMapping.txt","s01_S2C_47_off27100_InfoGatewayMapping.txt","s01_C2S_14_off5172_FilterGatewayMapping.txt")
foreach ($t in $targets) {
    $path = Join-Path $dir $t
    if (-not (Test-Path $path)) { continue }
    Write-Output ""
    Write-Output "########## $t ##########"
    $data = [byte[]](Get-Content $path -Encoding Byte)
    $i = 0
    while ($i -lt $data.Length - 3) {
        if ($data[$i] -eq 0x74 -and $data[$i+2] -ge 1) {   # TC_STRING 0x74 + len2
            $len = (($data[$i+1] -shl 8) -bor $data[$i+2])
            if ($len -ge 1 -and $i + 3 + $len -le $data.Length) {
                $s = [System.Text.Encoding]::UTF8.GetString($data, $i+3, $len)
                if ($s -match '[\x20-\x7E]') {
                    # chi in chuoi co y nghia (khong phai ten field/class)
                    if ($s -notmatch '^[\.\w/\(\)\[\];]+$' -or $s -match '^\d|^[0-9;],|^1;|^0;') {
                        Write-Output ("  [$i len=$len] '" + $s + "'")
                    }
                }
                $i += 3 + $len
                continue
            }
        }
        $i++
    }
    # hien them byte quanh chi so thay chuoi prefix
    $hexAll = [BitConverter]::ToString($data) -replace '-',''
    $pos = $hexAll.IndexOf("313B313B")  # "1;1;"
    if ($pos -ge 0) {
        $bpos = [int]($pos/2)
        Write-Output ""
        Write-Output "  bytes xung quanh '1;1;':"
        $start = [Math]::Max(0, $bpos-20); $end = [Math]::Min($data.Length, $bpos+60)
        Write-Output ("    " + ([BitConverter]::ToString($data[$start..($end-1)])))
    }
}

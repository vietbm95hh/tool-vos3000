# =====================================================================
#  VOS3000 - BAT GOI TIN CLIENT <-> SERVER  (KHONG CAN SUA HOSTS/ROUTE)
#
#  Nguyen ly:
#    - Proxy listen tren 0.0.0.0:1202 (bat moi IP cua may nay)
#    - Trong man hinh LOGIN VOS3000, doi "Server" thanh 127.0.0.1
#    - Traffic client se di qua proxy -> proxy chuyen sang server that
#    - Ctrl+C de ket thuc - KHONG co gi phai khoi phuc
#
#  Chay bang quyen Admin de dam bao lang nghe port duoc phep.
# =====================================================================

$ErrorActionPreference = "Stop"

$TargetIP   = "172.29.101.32"
$TargetPort = 1202
$ProxyExe   = Join-Path $PSScriptRoot "bin\Debug\net8.0\Vos3000Proxy.exe"
$CaptureDir = Join-Path $PSScriptRoot "bin\Debug\net8.0\capture"

Write-Host ""
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  VOS3000 PACKET CAPTURE (simple method)" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host ""
    Write-Host "  LOI: Phai chay bang quyen ADMIN!" -ForegroundColor Red
    Read-Host "Nhan Enter de thoat"; exit 1
}

if (-not (Test-Path $ProxyExe)) {
    Write-Host "  LOI: Khong thay $ProxyExe" -ForegroundColor Red
    Write-Host "  Chay truoc: dotnet build" -ForegroundColor Yellow
    Read-Host "Nhan Enter de thoat"; exit 1
}

# ---------- Don dep di tich cua lan thu loopback (neu con sot) ----------
if ((Get-NetIPAddress -IPAddress $TargetIP -ErrorAction SilentlyContinue).InterfaceAlias -like "*Loopback*") {
    Write-Host "  Phat hien IP $TargetIP con tren loopback -> don dep..." -ForegroundColor Yellow
    Get-NetRoute -DestinationPrefix "$TargetIP/32" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-NetRoute -DestinationPrefix $_.DestinationPrefix -InterfaceIndex $_.ifIndex -Confirm:$false -ErrorAction SilentlyContinue }
    cmd /c "route delete $TargetIP 2>nul" | Out-Null
    netsh interface ipv4 delete address "Loopback Pseudo-Interface 1" $TargetIP | Out-Null
    Write-Host "  Da xoa IP $TargetIP khoi loopback." -ForegroundColor Green
}

# ---------- Xoa log cu ----------
if (Test-Path $CaptureDir) {
    $old = Get-ChildItem $CaptureDir -Filter "*.log" -ErrorAction SilentlyContinue
    if ($old) {
        Remove-Item $old.FullName -Force -ErrorAction SilentlyContinue
        Write-Host "  Da xoa $($old.Count) log cu" -ForegroundColor DarkGray
    }
}

# ---------- Huong dan ----------
Write-Host ""
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  TRONG VOS3000 CLIENT - MAN HINH LOGIN:" -ForegroundColor Green
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "   Server : 127.0.0.1        <- DOI CHO NAY (khong phai 172.29.101.32)" -ForegroundColor Yellow
Write-Host "   User   : nhu binh thuong" -ForegroundColor White
Write-Host "   Pass   : nhu binh thuong" -ForegroundColor White
Write-Host ""
Write-Host "   Sau khi login:" -ForegroundColor White
Write-Host "   1. Vao Mapping Prefix -> Data Configuration" -ForegroundColor White
Write-Host "   2. Bam Add, nhap 0999, OK" -ForegroundColor White
Write-Host "   3. Bam Apply" -ForegroundColor Yellow
Write-Host "   4. Dong client (Task Manager -> End task)" -ForegroundColor White
Write-Host "   5. Quay lai day bam Ctrl+C" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Log: $CaptureDir" -ForegroundColor DarkGray
Write-Host ""

try {
    & $ProxyExe --target-ip $TargetIP --target-port $TargetPort
}
finally {
    ipconfig /flushdns | Out-Null
}

Write-Host ""
Write-Host "  File log da ghi:" -ForegroundColor Cyan
Get-ChildItem $CaptureDir -Filter "*.log" -ErrorAction SilentlyContinue |
    Select-Object Name, Length | Format-Table -AutoSize

Read-Host "Nhan Enter de thoat"

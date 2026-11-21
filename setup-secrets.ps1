# Nhập các bí mật TUỲ CHỌN cho máy dev (YouTube API key, Gmail app password) vào User Secrets.
# Khoá JWT KHÔNG cần nhập: backend tự sinh lần chạy đầu (Development).
# Cách chạy (trong thư mục chứa file này):  powershell -ExecutionPolicy Bypass -File .\setup-secrets.ps1
# Ô nhập bị ẩn, giá trị không hiện ra màn hình và không vào lịch sử lệnh. Bỏ trống + Enter = giữ nguyên.

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ePriTrackerBackend'

function Read-Secret([string]$label) {
    $secure = Read-Host "$label (Enter để bỏ qua)" -AsSecureString
    return [Net.NetworkCredential]::new('', $secure).Password
}

function Set-Secret([string]$name, [string]$value) {
    dotnet user-secrets set $name $value --project $project | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Không lưu được $name" }
    Write-Host "  Đã lưu $name" -ForegroundColor Green
}

Write-Host "ePriTracker – cấu hình bí mật cho máy dev" -ForegroundColor Cyan
Write-Host "(JWT tự sinh khi chạy backend lần đầu, không cần nhập)`n"

$youtube = Read-Secret 'YouTube Data API v3 key'
if ($youtube) { Set-Secret 'YouTubeApiKey' $youtube }

$smtp = Read-Secret 'Gmail app password (gửi email thông báo)'
if ($smtp) { Set-Secret 'SmtpSettings:Password' $smtp }

Write-Host "`nCác khoá đang có (chỉ hiện tên):"
dotnet user-secrets list --project $project | ForEach-Object { '  ' + ($_ -split ' = ')[0] }

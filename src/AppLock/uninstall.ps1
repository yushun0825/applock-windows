# アプリロックを Windows から削除する(「設定」>「アプリ」の「アンインストール」から呼ばれる)
# 動いているアプリロックを終了し(設定で求めていれば Windows Hello で確かめる)、ファイル・設定・登録を消す。
$ErrorActionPreference = 'Stop'
$installDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $installDir 'AppLock.exe'

Write-Host 'アプリロックを削除しています…'

# 動いていれば終了してもらう(止めていたアプリはすべて動かしてから終わる)
$running = Get-Process -Name 'AppLock' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Host '動いているアプリロックを終了します。認証を求められたら、確かめてください。'
    & $exe --quit
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-Process -Id $running.Id -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Id $running.Id -ErrorAction SilentlyContinue) {
        Write-Host 'アプリロックが終了しなかったため、削除をやめました。通知領域のアイコンから終了してから、もう一度お試しください。'
        exit 1
    }
}

# サインイン時の起動・「アプリ」の一覧・スタート メニュー
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AppLock' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name 'AppLock' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AppLock' -Recurse -ErrorAction SilentlyContinue
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'アプリロック.lnk'
Remove-Item -Path $shortcut -ErrorAction SilentlyContinue

# 設定と記録
Remove-Item -Path (Join-Path $env:LOCALAPPDATA 'AppLock') -Recurse -Force -ErrorAction SilentlyContinue

# アプリ本体(このスクリプトも含む)
Set-Location $env:TEMP
Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'アプリロックを削除しました。'

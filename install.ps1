# アプリロック(Windows)を入れる・新しい版に入れ替える。
#
#   irm https://raw.githubusercontent.com/yushun0825/applock-windows/main/install.ps1 | iex
#
# GitHub の Releases から最新の版を取ってきて(SHA-256 で確かめる)、%LOCALAPPDATA%\Programs\AppLock に入れ、
# スタート メニューと「設定」>「アプリ」に登録して起動する。管理者の権限は使わない。
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $repo = 'yushun0825/applock-windows'

    if ([Environment]::OSVersion.Version.Build -lt 22000) { throw 'アプリロックには Windows 11 が必要です。' }
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'アプリロックには 64 ビットの Windows が必要です。' }

    # x64 のエミュレーションの中から呼ばれても、PC の本当の種類で選ぶ
    $native = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -ErrorAction SilentlyContinue).PROCESSOR_ARCHITECTURE
    $rid = if ($native -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $asset = "AppLock-$rid.zip"
    $base = "https://github.com/$repo/releases/latest/download"

    $temp = Join-Path $env:TEMP ('AppLock-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Write-Host "アプリロックをダウンロードしています($rid)…"
        $zip = Join-Path $temp $asset
        $sums = Join-Path $temp 'SHA256SUMS.txt'
        Invoke-WebRequest -Uri "$base/$asset" -OutFile $zip -UseBasicParsing
        Invoke-WebRequest -Uri "$base/SHA256SUMS.txt" -OutFile $sums -UseBasicParsing
        $expected = Get-Content $sums | Where-Object { $_ -match [regex]::Escape($asset) } | ForEach-Object { ($_ -split '\s+')[0] } | Select-Object -First 1
        $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash
        if (-not $expected -or $expected -ne $actual) { throw 'ダウンロードしたファイルが壊れています。もう一度お試しください。' }

        $dir = Join-Path $env:LOCALAPPDATA 'Programs\AppLock'
        $exe = Join-Path $dir 'AppLock.exe'

        # 動いていれば終了してもらう(設定で求めていれば、Windows Hello で確かめる)
        $running = Get-Process -Name 'AppLock' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
        if ($running) {
            Write-Host '動いているアプリロックを終了します。認証を求められたら、確かめてください。'
            & $exe --quit
            foreach ($process in $running) {
                if (-not $process.WaitForExit(120000)) { throw 'アプリロックが終了しなかったため、入れ替えをやめました。' }
            }
        }

        $unpacked = Join-Path $temp 'files'
        Expand-Archive -Path $zip -DestinationPath $unpacked -Force
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Copy-Item -Path (Join-Path $unpacked '*') -Destination $dir -Recurse -Force

        # スタート メニュー
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'アプリロック.lnk'))
        $link.TargetPath = $exe
        $link.WorkingDirectory = $dir
        $link.Description = '選んだアプリを、開くときに Windows Hello で確かめます'
        $link.Save()

        # 「設定」>「アプリ」(ここからアンインストールできる)
        $file = [Version](Get-Item $exe).VersionInfo.FileVersion
        $version = '{0}.{1}.{2}' -f $file.Major, $file.Minor, $file.Build
        $sizeKb = [int]((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AppLock'
        New-Item -Path $key -Force | Out-Null
        Set-ItemProperty -Path $key -Name 'DisplayName' -Value 'アプリロック'
        Set-ItemProperty -Path $key -Name 'DisplayVersion' -Value $version
        Set-ItemProperty -Path $key -Name 'Publisher' -Value 'yushun0825'
        Set-ItemProperty -Path $key -Name 'DisplayIcon' -Value "$exe,0"
        Set-ItemProperty -Path $key -Name 'InstallLocation' -Value $dir
        Set-ItemProperty -Path $key -Name 'URLInfoAbout' -Value "https://github.com/$repo"
        Set-ItemProperty -Path $key -Name 'UninstallString' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dir\uninstall.ps1`""
        Set-ItemProperty -Path $key -Name 'NoModify' -Value 1 -Type DWord
        Set-ItemProperty -Path $key -Name 'NoRepair' -Value 1 -Type DWord
        Set-ItemProperty -Path $key -Name 'EstimatedSize' -Value $sizeKb -Type DWord

        Start-Process -FilePath $exe
        Write-Host "アプリロック $version を入れました。スタート メニューの「アプリロック」からも開けます。"
    }
    finally {
        Remove-Item -Path $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

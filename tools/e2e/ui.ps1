# 画面の確かめ: 設定の窓を出す(-Action start)・「アプリを追加」を開く(add)・閉じる(stop)
param(
    [string] $Action = 'start',
    [string] $Theme = '',
    [string] $Exe = 'C:\Users\Public\AppLockTest\AppLock.exe',
    [string] $Data = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'out')
)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Data 'appdata-ui'
switch ($Action) {
    'start' {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $dir | Out-Null
        $apps = @(
            '{"name":"文字コード表","exeName":"charmap.exe","path":"C:\\Windows\\System32\\charmap.exe"}',
            '{"name":"メモ帳","exeName":"Notepad.exe","packageFamilyName":"Microsoft.WindowsNotepad_8wekyb3d8bbwe","appUserModelId":"Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"}',
            '{"name":"Microsoft Edge","exeName":"msedge.exe","path":"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe"}'
        ) -join ','
        [IO.File]::WriteAllText((Join-Path $dir 'settings.json'), '{"apps":[' + $apps + '],"relockRule":"afterInterval","relockMinutes":5,"toldAboutTray":true}')
        $env:APPLOCK_DATA_DIR = $dir
        $env:APPLOCK_FAKE_AUTH = 'pass:800'
        $env:APPLOCK_THEME = $Theme
        Start-Process -FilePath $Exe
    }
    'unlock' {
        # 文字コード表を開いて解除する(偽の認証)
        Start-Process charmap.exe
        Start-Sleep -Seconds 3
        $shell = New-Object -ComObject WScript.Shell
        $null = $shell.AppActivate('アプリロック')
    }
    'add' {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [Windows.Automation.AutomationElement]::RootElement
        $window = $root.FindFirst([Windows.Automation.TreeScope]::Children,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'アプリロック')))
        function Invoke-Named([string] $name) {
            $element = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
            if ($element) { $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
            return $false
        }
        if (Invoke-Named '認証…') { Start-Sleep -Seconds 2 }
        $null = Invoke-Named 'アプリを追加'
    }
    'stop' {
        & $Exe --quit
        Start-Sleep -Seconds 2
        Get-Process -Name 'AppLock' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force
        Get-Process -Name 'charmap' -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

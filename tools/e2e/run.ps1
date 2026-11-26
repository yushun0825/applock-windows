# AppLock end-to-end checks on Windows (run the testing build: -p:AppLockTesting=true).
# Each scenario writes one line to results.txt; AppLock's own log goes to appdata-<scenario>\logs.
# The window watcher (watch.ps1) samples every ~2 ms whether the target app has a window on screen.
param(
    [string] $Exe = 'C:\Users\Public\AppLockTest\AppLock.exe',
    [string] $Data = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'out'),
    [string[]] $Scenarios = @('pass', 'cancel-quit', 'cancel-keep', 'existing', 'notepad', 'edge', 'idle')
)
$ErrorActionPreference = 'Stop'
$Scenarios = @($Scenarios | ForEach-Object { $_ -split ',' })
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$results = Join-Path $Data 'results.txt'
New-Item -ItemType Directory -Force $Data | Out-Null

$charmap = '{"name":"Character Map","exeName":"charmap.exe","path":"C:\\Windows\\System32\\charmap.exe"}'
$notepad = '{"name":"Notepad","exeName":"Notepad.exe","packageFamilyName":"Microsoft.WindowsNotepad_8wekyb3d8bbwe","appUserModelId":"Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"}'
$edge = '{"name":"Edge","exeName":"msedge.exe"}'

function Write-Result([string] $line) {
    Add-Content -Path $results -Value ((Get-Date -Format 'HH:mm:ss.fff') + ' ' + $line) -Encoding ASCII
}

function New-AppData([string] $name, [string] $apps, [bool] $quit = $true) {
    $dir = Join-Path $Data "appdata-$name"
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dir | Out-Null
    $json = '{"apps":[' + $apps + '],"relockRule":"whenAppQuits","quitAppOnFailure":' + $quit.ToString().ToLower() + ',"protectSelf":false,"toldAboutTray":true}'
    [IO.File]::WriteAllText((Join-Path $dir 'settings.json'), $json)
    return $dir
}

function Start-AppLock([string] $dir, [string] $fake) {
    $env:APPLOCK_DATA_DIR = $dir
    $env:APPLOCK_FAKE_AUTH = $fake
    $process = Start-Process -FilePath $Exe -ArgumentList '--background' -PassThru
    Start-Sleep -Milliseconds 2500
    return $process
}

function Stop-AppLock($process) {
    & $Exe --quit
    if (-not $process.WaitForExit(10000)) {
        $process.Kill()
        Write-Result '  WARN applock did not quit; killed'
    }
}

function Stop-Name([string] $name) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

function Start-Watch([string[]] $names, [double] $seconds, [string] $file) {
    Remove-Item "$file.ready" -ErrorAction SilentlyContinue
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'watch.ps1'), '-Names', ($names -join ','), '-Seconds', $seconds, '-Out', $file)
    $process = Start-Process powershell.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 200 -and -not (Test-Path "$file.ready"); $i++) { Start-Sleep -Milliseconds 50 }
    return $process
}

function Read-Watch([string] $file) {
    if (Test-Path $file) { return (Get-Content $file -TotalCount 1) } else { return 'no-watch-result' }
}

function Test-Suspended([string] $name) {
    $process = Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) { return 'gone' }
    $suspended = @($process.Threads | Where-Object { $_.ThreadState -eq 'Wait' -and $_.WaitReason -eq 'Suspended' }).Count
    return "threads=$($process.Threads.Count) suspended=$suspended"
}

Set-Content -Path $results -Value ('start ' + (Get-Date -Format s)) -Encoding ASCII

foreach ($scenario in $Scenarios) {
    try {
        switch ($scenario) {
            'pass' {
                # launch -> frozen before any window -> fake pass after 1.5 s -> window appears
                Stop-Name 'charmap'
                $app = Start-AppLock (New-AppData $scenario $charmap) 'pass:1500'
                $file = Join-Path $Data "watch-$scenario.txt"
                $watch = Start-Watch @('charmap') 6 $file
                Write-Result "pass launch"
                Start-Process charmap.exe
                $watch.WaitForExit()
                $alive = [bool](Get-Process charmap -ErrorAction SilentlyContinue)
                Write-Result "pass alive=$alive $(Read-Watch $file)"
                Stop-Name 'charmap'
                Stop-AppLock $app
            }
            'cancel-quit' {
                # launch -> canceled -> app terminated without ever showing a window
                Stop-Name 'charmap'
                $app = Start-AppLock (New-AppData $scenario $charmap) 'cancel:1500'
                $file = Join-Path $Data "watch-$scenario.txt"
                $watch = Start-Watch @('charmap') 5 $file
                Write-Result "cancel-quit launch"
                Start-Process charmap.exe
                $watch.WaitForExit()
                $alive = [bool](Get-Process charmap -ErrorAction SilentlyContinue)
                Write-Result "cancel-quit alive=$alive $(Read-Watch $file)"
                Stop-Name 'charmap'
                Stop-AppLock $app
            }
            'cancel-keep' {
                # launch -> canceled, quit off -> stays frozen and hidden; quitting AppLock resumes it
                Stop-Name 'charmap'
                $app = Start-AppLock (New-AppData $scenario $charmap $false) 'cancel:1500'
                $file = Join-Path $Data "watch-$scenario.txt"
                $watch = Start-Watch @('charmap') 5 $file
                Write-Result "cancel-keep launch"
                Start-Process charmap.exe
                $watch.WaitForExit()
                Write-Result "cancel-keep while-locked $(Test-Suspended 'charmap') $(Read-Watch $file)"
                $file2 = Join-Path $Data "watch-$scenario-after.txt"
                $watch2 = Start-Watch @('charmap') 4 $file2
                Stop-AppLock $app
                $watch2.WaitForExit()
                Write-Result "cancel-keep after-quit $(Test-Suspended 'charmap') $(Read-Watch $file2)"
                Stop-Name 'charmap'
            }
            'existing' {
                # app already open when AppLock starts -> hidden without asking; launching it again asks, then both windows return
                Stop-Name 'charmap'
                Start-Process charmap.exe
                Start-Sleep -Milliseconds 1500
                $file = Join-Path $Data "watch-$scenario.txt"
                $watch = Start-Watch @('charmap') 4 $file
                $app = Start-AppLock (New-AppData $scenario $charmap) 'pass:1500'
                $watch.WaitForExit()
                Write-Result "existing hidden-at-start $(Read-Watch $file)"
                $file2 = Join-Path $Data "watch-$scenario-relaunch.txt"
                $watch2 = Start-Watch @('charmap') 5 $file2
                Write-Result "existing relaunch"
                Start-Process charmap.exe
                $watch2.WaitForExit()
                Write-Result "existing after-unlock count=$(@(Get-Process charmap -ErrorAction SilentlyContinue).Count) $(Read-Watch $file2)"
                Stop-Name 'charmap'
                Stop-AppLock $app
            }
            'notepad' {
                # packaged app (WinUI): matched by package family + exe name
                Stop-Name 'notepad'
                $app = Start-AppLock (New-AppData $scenario $notepad) 'pass:2000'
                $file = Join-Path $Data "watch-$scenario.txt"
                $watch = Start-Watch @('notepad') 7 $file
                Write-Result "notepad launch"
                Start-Process notepad.exe
                $watch.WaitForExit()
                Write-Result "notepad alive=$([bool](Get-Process notepad -ErrorAction SilentlyContinue)) $(Read-Watch $file)"
                Stop-Name 'notepad'
                Stop-AppLock $app
            }
            'edge' {
                # multi-process browser: one prompt for the launch, child processes are not launches
                $app = Start-AppLock (New-AppData $scenario $edge) 'pass:1500'
                $file = Join-Path $Data "watch-$scenario.txt"
                $watch = Start-Watch @('msedge') 9 $file
                Write-Result "edge launch"
                Start-Process msedge.exe -ArgumentList '--no-first-run', 'about:blank'
                $watch.WaitForExit()
                Write-Result "edge processes=$(@(Get-Process msedge -ErrorAction SilentlyContinue).Count) $(Read-Watch $file)"
                Stop-Name 'msedge'
                Stop-AppLock $app
            }
            'hello-start' {
                # real Windows Hello (no fake): start AppLock and leave it running
                Stop-Name 'charmap'
                $null = Start-AppLock (New-AppData 'hello' $charmap) ''
                Write-Result 'hello-start ok'
            }
            'hello-launch' {
                $file = Join-Path $Data 'watch-hello.txt'
                $null = Start-Watch @('charmap') 15 $file
                Write-Result 'hello-launch'
                Start-Process charmap.exe
            }
            'hello-finish' {
                $file = Join-Path $Data 'watch-hello.txt'
                for ($i = 0; $i -lt 300 -and -not (Test-Path $file); $i++) { Start-Sleep -Milliseconds 100 }
                Write-Result "hello-finish alive=$([bool](Get-Process charmap -ErrorAction SilentlyContinue)) $(Read-Watch $file)"
                $app = Get-Process -Name 'AppLock' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Select-Object -First 1
                if ($app) { Stop-AppLock $app }
                Stop-Name 'charmap'
            }
            'idle' {
                # CPU used while waiting with one locked app configured
                $app = Start-AppLock (New-AppData $scenario $charmap) 'pass:1000'
                Start-Sleep -Seconds 30
                $before = (Get-Process -Id $app.Id).TotalProcessorTime.TotalMilliseconds
                Start-Sleep -Seconds 60
                $process = Get-Process -Id $app.Id
                $after = $process.TotalProcessorTime.TotalMilliseconds
                Write-Result ("idle cpu_ms_per_60s={0:N0} percent_of_one_core={1:N2} working_set_mb={2} private_mb={3}" -f ($after - $before), (($after - $before) / 60000 * 100), [math]::Round($process.WorkingSet64 / 1MB), [math]::Round($process.PrivateMemorySize64 / 1MB))
                Stop-AppLock $app
            }
        }
    }
    catch {
        Write-Result "$scenario ERROR $($_.Exception.Message)"
    }
}
Write-Result 'done'

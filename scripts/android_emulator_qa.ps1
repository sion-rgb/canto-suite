param(
    [string]$AvdName = "CantoSuite_API35",
    [int]$RamMiB = 4096,
    [string]$ApkPath = ""
)

$ErrorActionPreference = "Stop"

if (-not $env:JAVA_HOME -and -not (Get-Command java -ErrorAction SilentlyContinue)) {
    $androidStudioJbr = Join-Path $env:ProgramFiles "Android\Android Studio\jbr"
    if (Test-Path -LiteralPath (Join-Path $androidStudioJbr "bin\java.exe")) {
        $env:JAVA_HOME = $androidStudioJbr
    } else {
        throw "JAVA_HOME is unset and Android Studio's bundled JBR was not found."
    }
}

$sdkRoot = if ($env:ANDROID_SDK_ROOT) {
    $env:ANDROID_SDK_ROOT
} elseif ($env:ANDROID_HOME) {
    $env:ANDROID_HOME
} else {
    Join-Path $env:LOCALAPPDATA "Android\Sdk"
}

$adb = Join-Path $sdkRoot "platform-tools\adb.exe"
$emulator = Join-Path $sdkRoot "emulator\emulator.exe"
$sdkManager = Join-Path $sdkRoot "cmdline-tools\latest\bin\sdkmanager.bat"
$avdManager = Join-Path $sdkRoot "cmdline-tools\latest\bin\avdmanager.bat"

foreach ($tool in @($adb, $emulator, $sdkManager, $avdManager)) {
    if (-not (Test-Path -LiteralPath $tool)) {
        throw "Android QA tool not found: $tool"
    }
}

Write-Host "== adb devices -l =="
& $adb devices -l
Write-Host "== emulator -list-avds =="
$installedAvds = @(& $emulator -list-avds | Where-Object { $_.Trim() })
$installedAvds | ForEach-Object { Write-Host $_ }
Write-Host "== installed system images =="
$sdkLines = @(& $sdkManager --list_installed)
$sdkLines | Select-String -Pattern "system-images;"
Write-Host "== avdmanager availability =="
& $avdManager list target | Select-Object -First 20

if ($installedAvds.Count -eq 0) {
    $systemImage = $sdkLines |
        ForEach-Object { if ($_ -match "^\s*(system-images;android-\d+;google_apis;x86_64)\s") { $Matches[1] } } |
        Sort-Object -Descending |
        Select-Object -First 1
    if (-not $systemImage) {
        throw "No installed Google APIs x86_64 system image is available for unattended AVD creation."
    }

    Write-Host "Creating $AvdName from $systemImage"
    "no" | & $avdManager create avd --force --name $AvdName --package $systemImage --device "pixel_6"
    $selectedAvd = $AvdName
} else {
    $selectedAvd = if ($installedAvds -contains $AvdName) { $AvdName } else { $installedAvds[0] }
}

$avdConfig = Join-Path $env:USERPROFILE ".android\avd\$selectedAvd.avd\config.ini"
if (Test-Path -LiteralPath $avdConfig) {
    $config = Get-Content -Raw -LiteralPath $avdConfig
    if ($config -match "(?m)^hw\.ramSize\s*=.*$") {
        $config = $config -replace "(?m)^hw\.ramSize\s*=.*$", "hw.ramSize = $RamMiB"
    } else {
        $config += "`r`nhw.ramSize = $RamMiB`r`n"
    }
    Set-Content -LiteralPath $avdConfig -Value $config -Encoding utf8NoBOM
}

$emulatorSerial = (& $adb devices) |
    ForEach-Object { if ($_ -match "^(emulator-\d+)\s+device$") { $Matches[1] } } |
    Select-Object -First 1

if (-not $emulatorSerial) {
    Write-Host "Booting $selectedAvd with host audio enabled"
    Start-Process -FilePath $emulator -WindowStyle Hidden -ArgumentList @(
        "-avd", $selectedAvd,
        "-no-snapshot-load",
        "-no-boot-anim",
        "-gpu", "auto",
        "-no-metrics",
        "-no-window",
        "-allow-host-audio"
    )

    $deadline = (Get-Date).AddMinutes(4)
    do {
        Start-Sleep -Seconds 2
        $emulatorSerial = (& $adb devices) |
            ForEach-Object { if ($_ -match "^(emulator-\d+)\s+device$") { $Matches[1] } } |
            Select-Object -First 1
    } while (-not $emulatorSerial -and (Get-Date) -lt $deadline)
    if (-not $emulatorSerial) { throw "Emulator did not connect within four minutes." }
}

$bootDeadline = (Get-Date).AddMinutes(4)
do {
    $bootComplete = (& $adb -s $emulatorSerial shell getprop sys.boot_completed 2>$null).Trim()
    if ($bootComplete -eq "1") { break }
    Start-Sleep -Seconds 2
} while ((Get-Date) -lt $bootDeadline)
if ($bootComplete -ne "1") { throw "Emulator connected but Android did not finish booting." }

Write-Host "== emulator runtime =="
& $adb -s $emulatorSerial shell getprop ro.build.version.sdk
& $adb -s $emulatorSerial shell getprop ro.product.cpu.abilist
& $adb -s $emulatorSerial shell getprop ro.dalvik.vm.native.bridge
& $adb -s $emulatorSerial shell cat /proc/meminfo | Select-Object -First 3

if ($ApkPath) {
    $resolvedApk = (Resolve-Path -LiteralPath $ApkPath).Path
    Write-Host "Installing $resolvedApk"
    & $adb -s $emulatorSerial install -r $resolvedApk
}

Write-Host "Android emulator QA target ready: $emulatorSerial ($selectedAvd)"
Write-Host "Native Bridge execution is functional QA only; it is not ARM64 performance evidence."

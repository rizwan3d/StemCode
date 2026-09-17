[CmdletBinding()]
param(
    [string]$Tag = $env:StemCode_TAG,
    [string]$InstallDir,
    [string]$CommandName,
    [string]$WaitForProcessId = $env:StemCode_WAIT_FOR_PROCESS_ID
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Resolve the install directory: explicit -InstallDir wins, then StemCode_INSTALL_DIR
# (used by '/update' to replace the running binary in place), then the default location.
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = if (-not [string]::IsNullOrWhiteSpace($env:StemCode_INSTALL_DIR)) {
        $env:StemCode_INSTALL_DIR
    }
    else {
        Join-Path $env:LOCALAPPDATA 'Programs\StemCode\bin'
    }
}

# Resolve the installed command name the same way so '/update' keeps the running
# binary's filename. The '.exe' extension is appended below.
if ([string]::IsNullOrWhiteSpace($CommandName)) {
    $CommandName = if (-not [string]::IsNullOrWhiteSpace($env:StemCode_COMMAND_NAME)) {
        $env:StemCode_COMMAND_NAME
    }
    else {
        'stemcode'
    }
}

$Owner = 'rizwan3d'
$Repo = 'StemCode'
$AppName = 'StemCode.CLI'
$VoiceAppName = 'StemCode.Voice'
$ExecutableName = 'StemCode.CLI'
$VoiceExecutableName = 'stemcode-voice'
$AssetName = "$ExecutableName-win-x64.zip"
$VoiceAssetName = "$VoiceAppName-win-x64.zip"
$ChecksumsName = 'SHA256SUMS'
$TotalSteps = 7
$CurrentStep = 0
$InstallActivity = "Installing $AppName"

# Anonymous install analytics. Mirrors the in-product PostHog defaults so installs
# and usage land in the same project. Opt out with STEMCODE_TELEMETRY_DISABLED=1
# or the cross-tool DO_NOT_TRACK convention.
$TelemetryHost = 'https://us.i.posthog.com'
$TelemetryProjectToken = 'phc_AKZFSyU239kkQ5GQ2y4idb8MtFX96kVekgezgnsELHRk'
$TelemetryEvent = 'cli installed'

function Write-Status {
    param([string]$Message)

    Write-Host "[$AppName] $Message"
}

function Fail-Install {
    param([string]$Message)

    throw "[$AppName] $Message"
}

function Test-TelemetryEnabled {
    $optOut = if (-not [string]::IsNullOrWhiteSpace($env:STEMCODE_TELEMETRY_DISABLED)) {
        $env:STEMCODE_TELEMETRY_DISABLED
    }
    else {
        $env:DO_NOT_TRACK
    }

    if ($optOut -in @('1', 'true', 'TRUE', 'True', 'yes', 'YES', 'Yes', 'on', 'ON', 'On')) {
        return $false
    }

    return -not [string]::IsNullOrWhiteSpace($TelemetryProjectToken)
}

# Best-effort anonymous "installed" event. Never fails the install: all errors are
# swallowed and the request is bounded by a short timeout.
function Send-InstallTelemetry {
    param([string]$Tag)

    try {
        if (-not (Test-TelemetryEnabled)) {
            return
        }

        $isCi = (-not [string]::IsNullOrWhiteSpace($env:CI)) -or
            (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTIONS)) -or
            (-not [string]::IsNullOrWhiteSpace($env:GITLAB_CI)) -or
            (-not [string]::IsNullOrWhiteSpace($env:BITBUCKET_BUILD_NUMBER))

        $payload = @{
            api_key     = $TelemetryProjectToken
            event       = $TelemetryEvent
            distinct_id = [System.Guid]::NewGuid().ToString('N')
            properties  = @{
                '$lib'                = 'installer'
                install_method        = 'install.ps1'
                app_version           = if ([string]::IsNullOrWhiteSpace($Tag)) { 'unknown' } else { $Tag }
                os_family             = 'windows'
                platform              = 'win-x64'
                app_surface           = 'cli'
                execution_environment = if ($isCi) { 'ci' } else { 'local' }
                is_ci                 = [bool]$isCi
            }
        } | ConvertTo-Json -Compress -Depth 5

        $endpoint = "$TelemetryHost/i/v0/e/"
        Invoke-RestMethod -Uri $endpoint -Method Post -ContentType 'application/json' -Body $payload -TimeoutSec 5 | Out-Null
    }
    catch {
        # Telemetry must never affect installation.
    }
}

function Test-ProgressEnabled {
    $value = if ($env:STEMCODE_NO_PROGRESS) { $env:STEMCODE_NO_PROGRESS } else { $env:StemCode_NO_PROGRESS }
    if ($value -in @('1', 'true', 'TRUE', 'True', 'yes', 'YES', 'Yes')) {
        return $false
    }

    try {
        return -not [Console]::IsOutputRedirected
    }
    catch {
        return $true
    }
}

function Format-ByteSize {
    param([long]$Bytes)

    if ($Bytes -ge 1GB) {
        return '{0:N1} GiB' -f ($Bytes / 1GB)
    }

    if ($Bytes -ge 1MB) {
        return '{0:N1} MiB' -f ($Bytes / 1MB)
    }

    if ($Bytes -ge 1KB) {
        return '{0:N1} KiB' -f ($Bytes / 1KB)
    }

    return "$Bytes B"
}

function Write-InstallerProgress {
    param(
        [string]$Activity = $script:InstallActivity,
        [string]$Status = 'Working...',
        [int]$PercentComplete = 0,
        [switch]$Completed
    )

    if (-not (Test-ProgressEnabled)) {
        return
    }

    $previousProgressPreference = $script:ProgressPreference

    try {
        $script:ProgressPreference = 'Continue'

        if ($Completed) {
            Write-Progress -Activity $Activity -Completed
        }
        else {
            Write-Progress -Activity $Activity -Status $Status -PercentComplete $PercentComplete
        }
    }
    finally {
        $script:ProgressPreference = $previousProgressPreference
    }
}

function Start-InstallStep {
    param([string]$Message)

    $script:CurrentStep++
    $percent = [Math]::Min(99, [Math]::Floor((($script:CurrentStep - 1) / $script:TotalSteps) * 100))

    Write-Status "[$script:CurrentStep/$script:TotalSteps] $Message"
    Write-InstallerProgress -Status $Message -PercentComplete $percent
}

function Complete-InstallStep {
    param([string]$Message)

    $percent = [Math]::Min(100, [Math]::Floor(($script:CurrentStep / $script:TotalSteps) * 100))

    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        Write-Status "    $Message"
    }

    $status = if ([string]::IsNullOrWhiteSpace($Message)) { 'Working...' } else { $Message }
    Write-InstallerProgress -Status $status -PercentComplete $percent
}

function Complete-InstallerProgress {
    Write-InstallerProgress -Completed
}

function Get-LatestTag {
    $apiUrl = "https://api.github.com/repos/$Owner/$Repo/releases/latest"

    try {
        $response = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = "$AppName-installer" }
    }
    catch {
        Fail-Install "Unable to resolve the latest release tag from $apiUrl. Set StemCode_TAG and try again. $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace($response.tag_name)) {
        Fail-Install 'GitHub did not return a release tag.'
    }

    return [string]$response.tag_name
}

function Save-UrlToFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$Activity = 'Downloading',

        [switch]$ShowProgress
    )

    $lastError = $null

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $previousProgressPreference = $script:ProgressPreference

        try {
            $script:ProgressPreference = if ($ShowProgress -and (Test-ProgressEnabled)) { 'Continue' } else { 'SilentlyContinue' }
            $request = @{
                Uri = $Url
                OutFile = $Path
                Headers = @{ 'User-Agent' = "$AppName-installer" }
                ErrorAction = 'Stop'
            }

            if ($PSVersionTable.PSVersion.Major -lt 6) {
                $request.UseBasicParsing = $true
            }

            Invoke-WebRequest @request
            return
        }
        catch {
            $lastError = $_
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue

            if ($attempt -lt 3) {
                Write-Status "Download attempt $attempt failed; retrying..."
                Start-Sleep -Seconds (2 * $attempt)
            }
        }
        finally {
            $script:ProgressPreference = $previousProgressPreference
            if ($ShowProgress -and (Test-ProgressEnabled)) {
                Write-InstallerProgress -Activity $Activity -Completed
            }
        }
    }

    throw $lastError
}

function Get-ExpectedSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ChecksumsPath,

        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    foreach ($line in Get-Content -LiteralPath $ChecksumsPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -ne 2) {
            continue
        }

        $hash = $parts[0].ToLowerInvariant()
        $name = $parts[1].TrimStart([char]'*')
        if ($name.StartsWith('./', [StringComparison]::Ordinal)) {
            $name = $name.Substring(2)
        }

        if ($name -eq $FileName) {
            return $hash
        }
    }

    return $null
}

function Get-ReleaseAssetSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Tag,

        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    $metadataUrl = "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Tag"

    try {
        $release = Invoke-RestMethod -Uri $metadataUrl -Headers @{ 'User-Agent' = "$AppName-installer" }
    }
    catch {
        return $null
    }

    foreach ($asset in @($release.assets)) {
        if ([string]$asset.name -ne $FileName) {
            continue
        }

        $digest = [string]$asset.digest
        if ($digest.StartsWith('sha256:', [System.StringComparison]::OrdinalIgnoreCase)) {
            return $digest.Substring(7).ToLowerInvariant()
        }
    }

    return $null
}

function Test-ArchiveSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Tag,

        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,

        [Parameter(Mandatory = $true)]
        [string]$TempRoot,

        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    $checksumsUrl = "https://github.com/$Owner/$Repo/releases/download/$Tag/$ChecksumsName"
    $checksumsPath = Join-Path $TempRoot $ChecksumsName
    $expectedSha256 = $null

    Write-Status "Downloading $ChecksumsName..."
    try {
        Save-UrlToFile -Url $checksumsUrl -Path $checksumsPath
    }
    catch {
        $expectedSha256 = Get-ReleaseAssetSha256 -Tag $Tag -FileName $FileName

        if ([string]::IsNullOrWhiteSpace($expectedSha256)) {
            Fail-Install "Unable to download $ChecksumsName from $checksumsUrl, and no GitHub release metadata digest was found. Checksum verification is mandatory. $($_.Exception.Message)"
        }

        Write-Status "Using SHA256 digest from GitHub release metadata for $FileName."
    }

    if ([string]::IsNullOrWhiteSpace($expectedSha256)) {
        $expectedSha256 = Get-ExpectedSha256 -ChecksumsPath $checksumsPath -FileName $FileName
    }

    if ([string]::IsNullOrWhiteSpace($expectedSha256)) {
        Fail-Install "$ChecksumsName does not contain a checksum for $FileName."
    }

    if ($expectedSha256 -notmatch '^[0-9a-f]{64}$') {
        Fail-Install "$ChecksumsName contains an invalid SHA256 checksum for $FileName."
    }

    $actualSha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        Fail-Install "SHA256 verification failed for $FileName. Expected $expectedSha256, got $actualSha256."
    }

    Write-Status "Verified SHA256 checksum for $FileName."
}

function Test-PathContainsDirectory {
    param(
        [string]$PathValue,
        [string]$Directory
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $false
    }

    $normalizedTarget = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\')

    foreach ($entry in ($PathValue -split ';')) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        try {
            $normalizedEntry = [System.IO.Path]::GetFullPath($entry).TrimEnd('\')
        }
        catch {
            continue
        }

        if ($normalizedEntry -ieq $normalizedTarget) {
            return $true
        }
    }

    return $false
}

function ConvertTo-PowerShellLiteral {
    param([string]$Value)

    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-ValidProcessId {
    param([string]$Value)

    $parsedProcessId = 0
    if (
        -not [string]::IsNullOrWhiteSpace($Value) -and
        [int]::TryParse($Value, [ref]$parsedProcessId) -and
        $parsedProcessId -gt 0
    ) {
        return $parsedProcessId
    }

    return 0
}

function Test-SamePath {
    param(
        [string]$Left,
        [string]$Right
    )

    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) {
        return $false
    }

    try {
        $normalizedLeft = [System.IO.Path]::GetFullPath($Left).TrimEnd('\')
        $normalizedRight = [System.IO.Path]::GetFullPath($Right).TrimEnd('\')
    }
    catch {
        return $false
    }

    return $normalizedLeft -ieq $normalizedRight
}

function Get-ParentProcessId {
    param([int]$ProcessId)

    try {
        $processInfo = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        if ($null -ne $processInfo -and $processInfo.ParentProcessId -gt 0) {
            return [int]$processInfo.ParentProcessId
        }
    }
    catch {
    }

    return 0
}

function Resolve-WaitForProcessId {
    param(
        [string]$RequestedProcessId,
        [string]$DestinationPath
    )

    $requested = Get-ValidProcessId -Value $RequestedProcessId
    if ($requested -gt 0) {
        return $requested
    }

    $ancestorProcessId = $PID
    for ($index = 0; $index -lt 5; $index++) {
        $parentProcessId = Get-ParentProcessId -ProcessId $ancestorProcessId
        if ($parentProcessId -le 0) {
            break
        }

        try {
            $parentProcess = Get-Process -Id $parentProcessId -ErrorAction Stop
            $parentPath = $null
            try {
                $parentPath = $parentProcess.Path
            }
            catch {
            }

            if (
                $parentProcess.ProcessName -ieq $CommandName -or
                $parentProcess.ProcessName -ieq $ExecutableName -or
                (Test-SamePath -Left $parentPath -Right $DestinationPath)
            ) {
                return $parentProcessId
            }
        }
        catch {
        }

        $ancestorProcessId = $parentProcessId
    }

    return 0
}

function Start-DeferredInstall {
    param(
        [string]$SourcePath,
        [string]$SourceDirectory,
        [string]$DestinationPath,
        [string]$DestinationDirectory,
        [int]$ProcessId,
        [string]$CleanupRoot
    )

    $scriptPath = Join-Path $CleanupRoot 'complete-update.ps1'
    $logPath = Join-Path $CleanupRoot 'complete-update.log'
    $deferredScript = @'
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,

    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory,

    [int]$WaitForProcessId,

    [Parameter(Mandatory = $true)]
    [string]$CleanupRoot,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Log {
    param([string]$Message)

    $timestamp = [DateTimeOffset]::Now.ToString('o')
    Add-Content -LiteralPath $LogPath -Value "[$timestamp] $Message"
}

function Copy-WithRetry {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)
    while ($true) {
        try {
            New-Item -ItemType Directory -Path (Split-Path -Parent $DestinationPath) -Force | Out-Null
            Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
            return
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw
            }

            Start-Sleep -Seconds 1
        }
    }
}

function Copy-DirectoryWithRetry {
    param(
        [string]$SourceDirectory,
        [string]$DestinationDirectory
    )

    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)
    while ($true) {
        try {
            New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
            Get-ChildItem -LiteralPath $SourceDirectory -Force | Copy-Item -Destination $DestinationDirectory -Recurse -Force
            return
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw
            }

            Start-Sleep -Seconds 1
        }
    }
}

$completed = $false

try {
    Write-Log "Waiting for process $WaitForProcessId to exit before replacing $DestinationPath."

    if ($WaitForProcessId -gt 0) {
        try {
            Wait-Process -Id $WaitForProcessId -Timeout 86400 -ErrorAction SilentlyContinue
        }
        catch {
            Write-Log "Wait-Process warning: $($_.Exception.Message)"
        }
    }

    Copy-DirectoryWithRetry -SourceDirectory $SourceDirectory -DestinationDirectory $DestinationDirectory
    Copy-WithRetry -SourcePath $SourcePath -DestinationPath $DestinationPath
    Write-Log "Installed update to $DestinationPath."
    $completed = $true
}
catch {
    Write-Log "Update failed: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($completed -and (Test-Path -LiteralPath $CleanupRoot)) {
        Remove-Item -LiteralPath $CleanupRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
'@

    Set-Content -LiteralPath $scriptPath -Value $deferredScript -Encoding UTF8

    $command = "& " +
        (ConvertTo-PowerShellLiteral -Value $scriptPath) +
        " -SourcePath " +
        (ConvertTo-PowerShellLiteral -Value $SourcePath) +
        " -SourceDirectory " +
        (ConvertTo-PowerShellLiteral -Value $SourceDirectory) +
        " -DestinationPath " +
        (ConvertTo-PowerShellLiteral -Value $DestinationPath) +
        " -DestinationDirectory " +
        (ConvertTo-PowerShellLiteral -Value $DestinationDirectory) +
        " -WaitForProcessId $ProcessId -CleanupRoot " +
        (ConvertTo-PowerShellLiteral -Value $CleanupRoot) +
        " -LogPath " +
        (ConvertTo-PowerShellLiteral -Value $logPath)
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))

    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-EncodedCommand',
        $encodedCommand
    ) | Out-Null

    Write-Status "Update staged. Exit StemCode to finish replacing '$CommandName.exe'."
    Write-Status "Deferred update log: $logPath"
}

Write-Status 'StemCode CLI Installer'
Start-InstallStep 'Checking system requirements...'
$architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
if ($architecture -notin @('AMD64', 'x86_64')) {
    Fail-Install "Unsupported Windows architecture '$architecture'. This installer supports Windows x64 only."
}

Complete-InstallStep "Detected Windows $architecture."

Start-InstallStep 'Resolving release...'
if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = Get-LatestTag
}

Complete-InstallStep "Using $AppName and $VoiceAppName $Tag for win-x64."

$downloadUrl = "https://github.com/$Owner/$Repo/releases/download/$Tag/$AssetName"
$voiceDownloadUrl = "https://github.com/$Owner/$Repo/releases/download/$Tag/$VoiceAssetName"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "$AppName-install-$([System.Guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $tempRoot $AssetName
$voiceArchivePath = Join-Path $tempRoot $VoiceAssetName
$extractDir = Join-Path $tempRoot 'extract'
$voiceExtractDir = Join-Path $tempRoot 'voice-extract'
$destinationPath = Join-Path $InstallDir "$CommandName.exe"
$voiceDestinationDir = Join-Path $InstallDir 'voice'
$cleanupTempRoot = $true

try {
    Write-Status "Installing as '$CommandName'..."

    Start-InstallStep 'Preparing install directory...'
    Write-Status "Install directory: $InstallDir"

    New-Item -ItemType Directory -Path $tempRoot, $extractDir, $voiceExtractDir, $InstallDir -Force | Out-Null
    Complete-InstallStep 'Workspace ready.'

    Start-InstallStep 'Downloading release assets...'
    try {
        Save-UrlToFile -Url $downloadUrl -Path $archivePath -Activity "Downloading $AssetName" -ShowProgress
        Save-UrlToFile -Url $voiceDownloadUrl -Path $voiceArchivePath -Activity "Downloading $VoiceAssetName" -ShowProgress
    }
    catch {
        Fail-Install "Download failed. $($_.Exception.Message)"
    }

    $downloadedSize = (Get-Item -LiteralPath $archivePath).Length
    $voiceDownloadedSize = (Get-Item -LiteralPath $voiceArchivePath).Length
    Complete-InstallStep "Downloaded $AssetName ($(Format-ByteSize -Bytes $downloadedSize)) and $VoiceAssetName ($(Format-ByteSize -Bytes $voiceDownloadedSize))."

    Start-InstallStep 'Verifying downloads...'
    Test-ArchiveSha256 -Tag $Tag -ArchivePath $archivePath -TempRoot $tempRoot -FileName $AssetName
    Test-ArchiveSha256 -Tag $Tag -ArchivePath $voiceArchivePath -TempRoot $tempRoot -FileName $VoiceAssetName
    Complete-InstallStep 'Checksum verification passed.'

    Start-InstallStep 'Extracting archives...'
    Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force
    Expand-Archive -Path $voiceArchivePath -DestinationPath $voiceExtractDir -Force

    $sourcePath = Join-Path $extractDir "$ExecutableName.exe"
    $voiceSourcePath = Join-Path $voiceExtractDir "$VoiceExecutableName.exe"
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        Fail-Install "Expected executable '$ExecutableName.exe' was not found in $AssetName."
    }
    if (-not (Test-Path -LiteralPath $voiceSourcePath -PathType Leaf)) {
        Fail-Install "Expected executable '$VoiceExecutableName.exe' was not found in $VoiceAssetName."
    }

    Complete-InstallStep "Found $ExecutableName.exe and $VoiceExecutableName.exe."

    Start-InstallStep 'Installing command and Voice runtime...'
    if (Test-Path -LiteralPath $voiceDestinationDir) {
        Remove-Item -LiteralPath $voiceDestinationDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $voiceDestinationDir -Force | Out-Null
    Get-ChildItem -LiteralPath $voiceExtractDir -Force | Copy-Item -Destination $voiceDestinationDir -Recurse -Force
    Write-Status "Installed Voice runtime to $voiceDestinationDir"

    $waitProcessId = Resolve-WaitForProcessId -RequestedProcessId $WaitForProcessId -DestinationPath $destinationPath
    if ($waitProcessId -gt 0) {
        Start-DeferredInstall -SourcePath $sourcePath -SourceDirectory $extractDir -DestinationPath $destinationPath -DestinationDirectory $InstallDir -ProcessId $waitProcessId -CleanupRoot $tempRoot
        $cleanupTempRoot = $false
    }
    else {
        try {
            Get-ChildItem -LiteralPath $extractDir -Force | Copy-Item -Destination $InstallDir -Recurse -Force
            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        }
        catch {
            Fail-Install "Unable to replace '$destinationPath'. Close any running StemCode sessions and try again. $($_.Exception.Message)"
        }

        Write-Status "Installed '$CommandName.exe' to $destinationPath"
    }

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $pathUpdated = $false

    if (-not (Test-PathContainsDirectory -PathValue $userPath -Directory $InstallDir)) {
        $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) {
            $InstallDir
        }
        else {
            "$userPath;$InstallDir"
        }

        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
        $pathUpdated = $true
    }

    if ($pathUpdated) {
        Write-Status "Added '$InstallDir' to your user PATH. Restart your shell to use the new PATH entry."
    }
    else {
        Write-Status 'The install directory is already on your user PATH.'
    }

    Send-InstallTelemetry -Tag $Tag

    Complete-InstallStep 'Installation finished.'
    Complete-InstallerProgress
    Write-Status "Done. Run '$CommandName' to start StemCode."
}
finally {
    if ($cleanupTempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

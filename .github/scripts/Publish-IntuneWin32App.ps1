<#
.SYNOPSIS
Uploads a new installer version to an existing Intune Win32 app.

.DESCRIPTION
Packages the source folder with the Microsoft Win32 Content Prep Tool, uploads the
package as a new content version of the app, and commits it. The script then sets the
app version, the install and uninstall commands, and a detection rule that requires
this version or later. Devices with an older version fail detection, so Intune
installs the new package on them.

The script keeps the app's assignments and requirement rules. It replaces the
detection rules.

The caller must sign in with azure/login first. The signed-in identity needs the
DeviceManagementApps.ReadWrite.All application permission on Microsoft Graph.
#>
[CmdletBinding()]
param(
    # The object ID of the Win32 app in Intune.
    [Parameter(Mandatory)] [string] $AppId,
    # The folder to package. It holds the setup file and any helper files.
    [Parameter(Mandatory)] [string] $SourceFolder,
    # The file name of the installer inside the source folder.
    [Parameter(Mandatory)] [string] $SetupFile,
    # The app version, for example 1.2.0.
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $InstallCommandLine,
    [Parameter(Mandatory)] [string] $UninstallCommandLine,
    # The registry key that holds the installed version in its DisplayVersion value.
    [Parameter(Mandatory)] [string] $DetectionKeyPath,
    [Parameter(Mandatory)] [string] $PrepToolPath,
    [string] $OutputFolder = (Join-Path ([IO.Path]::GetTempPath()) 'intunewin')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$graph = 'https://graph.microsoft.com/beta'

$token = az account get-access-token --resource-type ms-graph --query accessToken --output tsv
if ($LASTEXITCODE -ne 0 -or -not $token) { throw 'The Azure CLI did not return a Microsoft Graph token.' }
$headers = @{ Authorization = "Bearer $token" }

function Invoke-Graph([string] $Method, [string] $Path, $Body) {
    $request = @{ Method = $Method; Uri = "$graph/$Path"; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $request.Body = ConvertTo-Json $Body -Depth 10 }
    Invoke-RestMethod @request
}

function Wait-UploadState([string] $FilePath, [string] $State) {
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        $file = Invoke-Graph GET $FilePath
        if ($file.uploadState -eq $State) { return $file }
        if ($file.uploadState -match 'Failed|TimedOut') { throw "Intune reported the upload state $($file.uploadState)." }
        Start-Sleep -Seconds 5
    }
    throw "Intune did not reach the upload state $State in 10 minutes."
}

# Fail early, before the upload, if the app ID is wrong.
$appPath = "deviceAppManagement/mobileApps/$AppId"
$app = Invoke-Graph GET $appPath
if ($app.'@odata.type' -ne '#microsoft.graph.win32LobApp') {
    throw "The Intune app $AppId is a $($app.'@odata.type'), not a Win32 app."
}
Write-Host "Updating '$($app.displayName)' from version '$($app.displayVersion)' to '$Version'."

# Package the source folder.
New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null
& $PrepToolPath -c $SourceFolder -s $SetupFile -o $OutputFolder -q
if ($LASTEXITCODE -ne 0) { throw "IntuneWinAppUtil.exe exited with code $LASTEXITCODE." }
$package = Join-Path $OutputFolder ([IO.Path]::ChangeExtension($SetupFile, '.intunewin'))

# The package is a zip. It holds the encrypted content and Detection.xml, which has the
# encryption keys that Intune needs for the commit.
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $reader = [IO.StreamReader]::new($zip.GetEntry('IntuneWinPackage/Metadata/Detection.xml').Open())
    try { [xml] $detection = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $info = $detection.ApplicationInfo
    $encryptedFile = Join-Path $OutputFolder $info.FileName
    [IO.Compression.ZipFileExtensions]::ExtractToFile(
        $zip.GetEntry("IntuneWinPackage/Contents/$($info.FileName)"), $encryptedFile, $true)
} finally {
    $zip.Dispose()
}

# Create a content version and a file entry for the upload.
$contentVersion = Invoke-Graph POST "$appPath/microsoft.graph.win32LobApp/contentVersions" @{}
$filesPath = "$appPath/microsoft.graph.win32LobApp/contentVersions/$($contentVersion.id)/files"
$file = Invoke-Graph POST $filesPath @{
    '@odata.type' = '#microsoft.graph.mobileAppContentFile'
    name          = $info.FileName
    size          = [int64] $info.UnencryptedContentSize
    sizeEncrypted = (Get-Item $encryptedFile).Length
    manifest      = $null
    isDependency  = $false
}
$filePath = "$filesPath/$($file.id)"
$storageUri = (Wait-UploadState $filePath 'azureStorageUriRequestSuccess').azureStorageUri

# Upload the encrypted content to Azure Storage in blocks, then commit the block list.
$blockSize = 6MB
$blockIds = [Collections.Generic.List[string]]::new()
$stream = [IO.File]::OpenRead($encryptedFile)
try {
    $buffer = [byte[]]::new($blockSize)
    while (($count = $stream.Read($buffer, 0, $blockSize)) -gt 0) {
        $block = [byte[]]::new($count)
        [Array]::Copy($buffer, $block, $count)
        $blockId = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($blockIds.Count.ToString('0000')))
        Invoke-RestMethod -Method Put -Uri "$storageUri&comp=block&blockid=$([uri]::EscapeDataString($blockId))" `
            -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } -ContentType 'application/octet-stream' -Body $block | Out-Null
        $blockIds.Add($blockId)
    }
} finally {
    $stream.Dispose()
}
$blockList = '<?xml version="1.0" encoding="utf-8"?><BlockList>' +
    (($blockIds | ForEach-Object { "<Latest>$_</Latest>" }) -join '') + '</BlockList>'
Invoke-RestMethod -Method Put -Uri "$storageUri&comp=blocklist" -ContentType 'application/xml' -Body $blockList | Out-Null
Write-Host "Uploaded $($blockIds.Count) blocks."

$encryption = $info.EncryptionInfo
Invoke-Graph POST "$filePath/commit" @{
    fileEncryptionInfo = @{
        encryptionKey        = $encryption.EncryptionKey
        macKey               = $encryption.MacKey
        initializationVector = $encryption.InitializationVector
        mac                  = $encryption.Mac
        profileIdentifier    = $encryption.ProfileIdentifier
        fileDigest           = $encryption.FileDigest
        fileDigestAlgorithm  = $encryption.FileDigestAlgorithm
    }
} | Out-Null
Wait-UploadState $filePath 'commitFileSuccess' | Out-Null

# Switch the app to the new content. Keep the requirement rules, and replace the
# detection rules with one that requires this version or later.
$rules = @($app.rules | Where-Object { $_ -and $_.ruleType -ne 'detection' }) + @{
    '@odata.type'        = '#microsoft.graph.win32LobAppRegistryRule'
    ruleType             = 'detection'
    check32BitOn64System = $false
    keyPath              = $DetectionKeyPath
    valueName            = 'DisplayVersion'
    operationType        = 'version'
    operator             = 'greaterThanOrEqual'
    comparisonValue      = $Version
}
Invoke-Graph PATCH $appPath @{
    '@odata.type'           = '#microsoft.graph.win32LobApp'
    committedContentVersion = $contentVersion.id
    fileName                = Split-Path $package -Leaf
    setupFilePath           = $SetupFile
    displayVersion          = $Version
    installCommandLine      = $InstallCommandLine
    uninstallCommandLine    = $UninstallCommandLine
    rules                   = $rules
} | Out-Null

Write-Host "Intune app '$($app.displayName)' now deploys version $Version (content version $($contentVersion.id))."

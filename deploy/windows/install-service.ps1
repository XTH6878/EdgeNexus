[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$PublishDirectory,

    [Parameter(Mandatory)]
    [string]$DataDirectory,

    [Parameter(Mandatory)]
    [string]$AdminUsername,

    [Parameter(Mandatory)]
    [string]$AdminPassword,

    [string]$ServiceName = 'SnetEdgeGateway',
    [string]$ListenUrl = 'http://127.0.0.1:5088'
)

$ErrorActionPreference = 'Stop'
$resolvedPublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$executablePath = Join-Path $resolvedPublishDirectory 'Snet.Edge.WebApi.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Gateway executable not found: $executablePath"
}

$resolvedDataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
if (-not (Test-Path -LiteralPath $resolvedDataDirectory)) {
    New-Item -ItemType Directory -Path $resolvedDataDirectory | Out-Null
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Uninstall or choose another name."
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Install Windows service')) {
    $binaryPath = '"{0}" --urls "{1}"' -f $executablePath, $ListenUrl
    New-Service -Name $ServiceName `
        -BinaryPathName $binaryPath `
        -DisplayName 'Snet Edge Industrial Gateway' `
        -Description 'Industrial PLC acquisition and MQTT edge gateway' `
        -StartupType Automatic | Out-Null

    & sc.exe config $ServiceName obj= 'NT AUTHORITY\LocalService' password= '' | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null

    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $environment = @(
        'ASPNETCORE_ENVIRONMENT=Production',
        "Gateway__DataDirectory=$resolvedDataDirectory",
        "Authentication__AdminUsername=$AdminUsername",
        "Authentication__AdminPassword=$AdminPassword",
        'Authentication__RequireHttps=false',
        'Swagger__Enabled=false'
    )
    New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null

    $accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        'NT AUTHORITY\LOCAL SERVICE',
        'Modify',
        'ContainerInherit,ObjectInherit',
        'None',
        'Allow')
    $acl = Get-Acl -LiteralPath $resolvedDataDirectory
    $acl.SetAccessRule($accessRule)
    Set-Acl -LiteralPath $resolvedDataDirectory -AclObject $acl

    Start-Service -Name $ServiceName
    Get-Service -Name $ServiceName
}

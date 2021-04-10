param(
    [Parameter(Mandatory)]
    [ValidateSet('Debug','Release')]
    [System.String]$Target,
    
    [Parameter(Mandatory)]
    [System.String]$TargetPath,
    
    [Parameter(Mandatory)]
    [System.String]$TargetAssembly,

    [Parameter(Mandatory)]
    [System.String]$ValheimPath,

    [Parameter(Mandatory)]
    [System.String]$ProjectPath
)

# Make sure Get-Location is the script path
Push-Location -Path (Split-Path -Parent $MyInvocation.MyCommand.Path)

# Test some preliminaries
("$TargetPath",
 "$ValheimPath",
 "$(Get-Location)\libraries"
) | % {
    if (!(Test-Path "$_")) {Write-Error -ErrorAction Stop -Message "$_ folder is missing"}
}

# Main Script
Write-Host "Publishing for $Target from $TargetPath"

if ($Target.Equals("Debug")) {
    Write-Host "Updating local installation in $ValheimPath"
    
    $name = "$TargetAssembly" -Replace('.dll')

    $plug = New-Item -Type Directory -Path "$ValheimPath\BepInEx\plugins\$name" -Force
    Write-Host "Copy $TargetAssembly to $plug"
    Copy-Item -Path "$TargetPath\$TargetAssembly" -Destination "$plug" -Force
    
    $mono = "$ValheimPath\MonoBleedingEdge\EmbedRuntime";
    Write-Host "Copy mono-2.0-bdwgc.dll to $mono"
    if (!(Test-Path -Path "$mono\mono-2.0-bdwgc.dll.orig")) {
        Copy-Item -Path "$mono\mono-2.0-bdwgc.dll" -Destination "$mono\mono-2.0-bdwgc.dll.orig" -Force
    }
    Copy-Item -Path "$(Get-Location)\libraries\Debug\mono-2.0-bdwgc.dll" -Destination "$mono" -Force

    $pdb = "$TargetPath\$name.pdb"
    if (Test-Path -Path "$pdb") {
        Write-Host "Copy Debug files for plugin $asm"
        Copy-Item -Path "$pdb" -Destination "$plug" -Force
        start "$(Get-Location)\libraries\Debug\pdb2mdb.exe" `"$plug\$TargetAssembly`"\
    }
}
if($Target.Equals("Release")) {
    Write-Host "Packaging for ThunderStore..."
    $Package="Package"
    $PackagePath=$ProjectPath+$Package

    Write-Host "$PackagePath\$TargetAssembly"
    Copy-Item -Path "$TargetPath\$TargetAssembly" -Destination "$PackagePath\plugins\$TargetAssembly"
    Copy-Item -Path "$PackagePath\README.md" -Destination "$ProjectPath\README.md"
    Compress-Archive -Path "$PackagePath\*" -DestinationPath "$TargetPath\$TargetAssembly.zip" -Force
}

        
    # set dnspy debugger env
    #$dnspy = '--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:56000,suspend=y,no-hide-debugger'
    #[Environment]::SetEnvironmentVariable('DNSPY_UNITY_DBG2','','User')

# Pop Location
Pop-Location
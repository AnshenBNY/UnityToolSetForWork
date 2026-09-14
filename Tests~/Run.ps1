param(
    [string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\2022.3.30f1c1\Editor\Unity.exe',
    [string]$Dotnet = 'C:\Program Files\dotnet\dotnet.exe',
    [string]$Csc = '',
    [string]$FocusedTestFile = '',
    [string]$ExecuteMethod = 'ToolSetRegression.Run',
    [string]$ResultFile = 'toolset-regression-results.txt',
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$toolRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = [IO.Path]::GetFullPath((Join-Path $toolRoot '../../..'))
$unityData = Join-Path (Split-Path -Parent $UnityEditor) 'Data'
$sdkPath = Join-Path (Split-Path -Parent $Dotnet) 'sdk'
if ([string]::IsNullOrWhiteSpace($Csc) -and (Test-Path -LiteralPath $sdkPath)) {
    $Csc = Get-ChildItem -LiteralPath $sdkPath -Directory |
        Sort-Object { [version]($_.Name.Split('-')[0]) } -Descending |
        ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($Csc)) { $Csc = Join-Path $unityData 'DotNetSdkRoslyn/csc.dll' }
if (-not (Test-Path -LiteralPath $Csc)) { throw '需要可用的 Roslyn csc.dll（.NET SDK 或 Unity DotNetSdkRoslyn）。' }
$runRoot = Join-Path $env:TEMP ('ToolSetRegression-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$runRoot/Assets/Editor","$runRoot/Assets/Support","$runRoot/Assets/FbxFixtures","$runRoot/Packages","$runRoot/ProjectSettings" | Out-Null
foreach ($assembly in @('Unity.Timeline.dll','UnityEngine.UI.dll')) {
    $source = Join-Path $projectRoot ('Library/ScriptAssemblies/' + $assembly)
    if (-not (Test-Path -LiteralPath $source)) { throw "请先用 Unity 成功编译项目：$source" }
    Copy-Item -LiteralPath $source -Destination "$runRoot/Assets/Support/$assembly"
}
if (-not $CompileOnly -and [string]::IsNullOrWhiteSpace($FocusedTestFile)) {
    $fbx = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Assets') -Filter 'fish_200004005_skin.fbx' -Recurse -File | Select-Object -First 1
    if (-not $fbx) { throw '缺少多 Mesh FBX 测试夹具 fish_200004005_skin.fbx。请提供至少两个单 subMesh 的 Mesh 对象的同名夹具。' }
    Copy-Item -LiteralPath $fbx.FullName -Destination "$runRoot/Assets/FbxFixtures/fish_200004005_skin.fbx"
}
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'Packages/manifest.json') -Raw | ConvertFrom-Json
$modules = [ordered]@{}
foreach ($property in $manifest.dependencies.PSObject.Properties) {
    if ($property.Name.StartsWith('com.unity.modules.')) { $modules[$property.Name] = $property.Value }
}
@{ dependencies = $modules } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$runRoot/Packages/manifest.json" -Encoding utf8
Copy-Item -LiteralPath (Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt') -Destination "$runRoot/ProjectSettings/ProjectVersion.txt"
$rsp = @('/nologo','/target:library','/langversion:9.0','/nostdlib+')
$rsp += '/out:"' + "$runRoot/Assets/Editor/ToolSetRegression.dll" + '"'
$references = @(
    Get-ChildItem -LiteralPath "$unityData/UnityReferenceAssemblies/unity-4.8-api" -Filter '*.dll' -Recurse -File
    Get-ChildItem -LiteralPath "$unityData/Managed/UnityEngine" -Filter '*.dll' -File
    Get-ChildItem -LiteralPath "$runRoot/Assets/Support" -Filter '*.dll' -File
)
$rsp += $references | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$rsp += Get-ChildItem -LiteralPath $toolRoot -Filter '*.cs' -File | ForEach-Object { '"' + $_.FullName + '"' }
$testFiles = if ([string]::IsNullOrWhiteSpace($FocusedTestFile)) {
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File
} else {
    Get-Item -LiteralPath (Join-Path $PSScriptRoot $FocusedTestFile)
}
$rsp += $testFiles | ForEach-Object { '"' + $_.FullName + '"' }
$rsp | Set-Content -LiteralPath "$runRoot/compile.rsp" -Encoding utf8
& $Dotnet $Csc "@$runRoot/compile.rsp"
if ($LASTEXITCODE -ne 0) { throw "测试编译失败，临时目录：$runRoot" }
if ($CompileOnly) {
    Write-Output "全部编辑器源码与回归测试编译通过：$runRoot/Assets/Editor/ToolSetRegression.dll"
    return
}
$arguments = @('-batchmode','-nographics','-projectPath',('"' + $runRoot + '"'),'-executeMethod',$ExecuteMethod,'-logFile',('"' + "$runRoot/unity.log" + '"'))
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "隔离测试已启动。PID=$($process.Id)"
Write-Output "项目及日志：$runRoot"
Write-Output "结果：$runRoot/$ResultFile"

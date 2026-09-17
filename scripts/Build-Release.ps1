param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDirectory,
    [string]$DotnetPath = "dotnet",
    [string]$Repository = "Tide-Rehtea/KaomojiWheel"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "source/KaomojiWheel/KaomojiWheel.csproj"
$tests = Join-Path $root "source/KaomojiWheel.SmokeTests/KaomojiWheel.SmokeTests.csproj"
$projectText = Get-Content -LiteralPath $project -Raw
if ($projectText -notmatch "<Version>$([regex]::Escape($Version))</Version>") { throw "KaomojiWheel.csproj 版本与参数 $Version 不一致。" }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root "dist/v$Version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$distRoot = [IO.Path]::GetFullPath((Join-Path $root "dist"))
if (-not $OutputDirectory.StartsWith($distRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "输出目录必须位于 dist 内。" }
if (Test-Path -LiteralPath $OutputDirectory) { throw "输出目录已经存在：$OutputDirectory" }

$publish = Join-Path $OutputDirectory "publish"
New-Item -ItemType Directory -Path $publish -Force | Out-Null
& $DotnetPath restore $project --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
& $DotnetPath build $project -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw "Release 构建失败。" }
& $DotnetPath run --project $tests -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "冒烟测试失败。" }
& $DotnetPath publish $project -c Release --no-restore --nologo -o $publish
if ($LASTEXITCODE -ne 0) { throw "发布失败。" }

$dataPath = Join-Path $publish "Data"
if (Test-Path -LiteralPath $dataPath) { throw "正式发布目录不得包含 Data。" }
$relativeFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\', '/') })
$relativeFiles += "installed-files.json"
$relativeFiles = @($relativeFiles | Sort-Object -Unique)
$installedJson = $relativeFiles | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $publish "installed-files.json"), $installedJson, [Text.UTF8Encoding]::new($false))

$packageName = "KaomojiWheel-v$Version-win-x64.zip"
$packagePath = Join-Path $OutputDirectory $packageName
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $packagePath -CompressionLevel Optimal
$sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
$tag = "v$Version"
$manifest = [ordered]@{
    version = $Version
    packageUrl = "https://github.com/$Repository/releases/download/$tag/$packageName"
    sha256 = $sha256
    releasePageUrl = "https://github.com/$Repository/releases/tag/$tag"
    releaseNotes = "查看 GitHub Release 页面了解此版本的完整更新内容。"
    publishedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$manifestJson = $manifest | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $OutputDirectory "latest.json"), $manifestJson, [Text.UTF8Encoding]::new($false))

Write-Host "发布包：$packagePath"
Write-Host "SHA-256：$sha256"
Write-Host "更新清单：$(Join-Path $OutputDirectory 'latest.json')"


param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    & dotnet build WindowsComputerUse.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet run --project tests/WindowsComputerUse.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Portable tests failed.' }
    $package = Join-Path $repo "artifacts/$Runtime"
    if (Test-Path $package) { Remove-Item -Recurse -Force $package }
    $projects = [ordered]@{
        server = 'src/WindowsComputerUse.Server/WindowsComputerUse.Server.csproj'
        worker = 'src/WindowsComputerUse.Worker/WindowsComputerUse.Worker.csproj'
        demo = 'samples/CuDemo/CuDemo.csproj'
        wpf = 'samples/CuWpfDemo/CuWpfDemo.csproj'
        tester = 'tests/WindowsComputerUse.Tests/WindowsComputerUse.Tests.csproj'
    }
    foreach ($part in $projects.Keys) {
        & dotnet publish $projects[$part] -c Release -r $Runtime --self-contained true -o "$package/$part" /p:PublishSingleFile=false
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $part" }
    }
    New-Item -ItemType Directory -Force "$package/scripts", "$package/.claude" | Out-Null
    Copy-Item scripts/Test-Windows.ps1, scripts/Register-ClaudeMcp.ps1 "$package/scripts/"
    Copy-Item .claude/skills "$package/.claude/" -Recurse
    Copy-Item Start-Test.cmd Setup-Claude.cmd "$package/"
    Copy-Item docs/windows-testing.md "$package/START-HERE.md"
    $commit = $env:WINDOWS_CU_SOURCE_COMMIT
    if (-not $commit) { $commit = (& git rev-parse HEAD).Trim() }
    $files = @(Get-ChildItem $package -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        @{ path = $_.FullName.Substring($package.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
    })
    $manifest = @{ sourceCommit = $commit; runtime = $Runtime; builtAt = [DateTime]::UtcNow.ToString('o'); files = $files }
    [IO.File]::WriteAllText("$package/package-manifest.json", ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $zip = Join-Path $repo "artifacts/windows-cu-$Runtime.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "Created $zip. Self-contained server, worker, fixtures and tester; no .NET SDK required on the test computer."
} finally { Pop-Location }

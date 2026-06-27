param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "S1DS-PlayerRobbery.csproj"
$testProjectPath = Join-Path $repoRoot "tests\S1DSPlayerRobbery.Tests\S1DSPlayerRobbery.Tests.csproj"

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host "==> $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

if (-not $SkipTests) {
    Invoke-Step "Shared contract tests" {
        dotnet run --project $testProjectPath
    }
}

Invoke-Step "Mono server build" {
    dotnet build $projectPath -c Mono_Server -p:AutomateLocalDeployment=false
}

Invoke-Step "Mono client build" {
    dotnet build $projectPath -c Mono_Client -p:AutomateLocalDeployment=false
}

Write-Host "Release validation passed."

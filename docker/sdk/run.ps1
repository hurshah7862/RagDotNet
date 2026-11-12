# Runs `dotnet <args>` in the ragdotnet-sdk:ocr image against this repository (or worktree), mounted at /work.
# Example: ./docker/sdk/run.ps1 test RagDotNet.slnx
# Arguments are passed as an array, so a `--` reaches dotnet instead of being swallowed by PowerShell.
param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $DotnetArgs)
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
docker run --rm `
    -v "${repo}:/work" -w /work `
    -v ragdotnet-nuget:/root/.nuget/packages `
    --add-host host.docker.internal:host-gateway `
    ragdotnet-sdk:ocr dotnet @DotnetArgs
exit $LASTEXITCODE

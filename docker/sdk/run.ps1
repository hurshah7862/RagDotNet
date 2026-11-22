# Runs `dotnet <args>` in the ragdotnet-sdk:ocr image against this repository (or worktree), mounted at /work.
# Example: ./docker/sdk/run.ps1 test RagDotNet.slnx
# Arguments are passed as an array, so a `--` reaches dotnet instead of being swallowed by PowerShell.
# The Docker socket is mounted so the store tests can start their containers (Testcontainers) next to this one;
# RAGDOTNET_INTEGRATION and RAGDOTNET_STORES_REQUIRED pass through from the calling shell.
param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $DotnetArgs)
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
docker run --rm `
    -v "${repo}:/work" -w /work `
    -v ragdotnet-nuget:/root/.nuget/packages `
    --add-host host.docker.internal:host-gateway `
    -v /var/run/docker.sock:/var/run/docker.sock `
    -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
    -e RAGDOTNET_INTEGRATION -e RAGDOTNET_STORES_REQUIRED `
    ragdotnet-sdk:ocr dotnet @DotnetArgs
exit $LASTEXITCODE

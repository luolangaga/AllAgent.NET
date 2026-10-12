$ErrorActionPreference = 'Stop'
$request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($args[0])) | ConvertFrom-Json
$literalArguments = @($request.args | ForEach-Object { [string]$_ })
& ([string]$request.exe) @literalArguments
if ($null -eq $LASTEXITCODE) { exit 0 }
exit $LASTEXITCODE

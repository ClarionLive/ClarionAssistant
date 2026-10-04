# Runs LspDiagnostics.SemanticPassTest.ps1 with NO Clarion version (06632787). See the -NoVersion note there.
# Exit: 0 pass, 1 fail, 2 could-not-run.
& (Join-Path $PSScriptRoot 'LspDiagnostics.SemanticPassTest.ps1') -NoVersion
exit $LASTEXITCODE

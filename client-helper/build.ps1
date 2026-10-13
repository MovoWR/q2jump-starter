# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Output,
    [switch]$Test
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework x64 C# compiler is required.'
}
$destination = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $destination) {
    throw 'Output already exists. Choose a new build directory.'
}
[IO.Directory]::CreateDirectory($destination) | Out-Null
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'Starter.*.cs' -File | Sort-Object Name | ForEach-Object FullName)
if ($sources.Count -ne 14) { throw 'Expected exactly 14 Starter client helper sources.' }
$common = @('/nologo', '/codepage:65001', '/langversion:5', '/platform:x64', '/optimize+', '/warnaserror+',
    '/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll')
$helper = Join-Path $destination 'Q2JUMP-Starter-Client.exe'
& $compiler @common '/target:exe' '/main:Q2JumpStarter.StarterClientProgram' ('/out:' + $helper) @sources
if ($LASTEXITCODE -ne 0) { throw 'Starter client helper compilation failed.' }

if ($Test) {
    foreach ($fixture in @('InstallationTests', 'GameDataTests', 'ManagedConfigurationTests', 'GitHubReleaseTests')) {
        $testSource = Join-Path $PSScriptRoot ('tests/' + $fixture + '.cs')
        $testOutput = Join-Path $destination ($fixture + '.exe')
        & $compiler @common '/target:exe' ('/main:Q2JumpStarter.' + $fixture) ('/out:' + $testOutput) @sources $testSource
        if ($LASTEXITCODE -ne 0) { throw "$fixture compilation failed." }
        & $testOutput
        if ($LASTEXITCODE -ne 0) { throw "$fixture failed." }
    }
}
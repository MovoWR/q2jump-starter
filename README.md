# Q2JUMP Starter

A Windows installer for Q2PRO Race and R1Q2 Race. Set up a new game folder or
update either client in an existing Quake II installation. Both clients can
share the same folder.

[Download q2jump-starter.exe](https://github.com/MovoWR/q2jump-starter/releases)

Requires 64-bit Windows 10 (build 17763 or newer) and an internet connection.
Q2PRO Race also needs the Microsoft Visual C++ x64 runtime; setup reports it
if missing.

## Install

1. Run `q2jump-starter.exe` and choose a new or existing installation.
   A new installation downloads the Quake II demo and official 3.20 update data.
2. Select Q2PRO Race, R1Q2 Race, or both.
3. Choose an empty folder for a new game, or the existing Quake II folder
   containing `baseq2/*.pak`.
4. Review any requested changes and install. The finish screen shows which
   clients are ready, with options to create shortcuts, launch a client, or
   open the game folder and setup log.

Starter preserves your settings, custom PAKs, and unrelated files. Downloads
are checksum-checked, and replaced client files are backed up for recovery.
Close running clients before installing. If setup is interrupted, rerun it
with the same folder to continue.

Fresh installations keep licenses, source references, and recovery files in
`.starter`. The game folders (`baseq2` and `jump`), executables, and required
runtime DLLs stay at the root. Existing installations keep their current layout.

Starter does not create an uninstaller. See [installation details](packaging/INSTALL.txt)
for recovery, silent setup, and compatibility notes. Original Quake II data
remains subject to the notices shown during setup.

## Build

Requires Windows, Python 3.11+, PowerShell, the .NET Framework x64 C# compiler,
and the [pinned build inputs](packaging/SOURCE.txt).

Run from the repository root, using fresh output directories:

```powershell
python -m unittest discover -s tests -p test_build.py -v
& ./client-helper/build.ps1 -Output build/client-helper-tests -Test
python scripts/build.py --output build/starter-next
```

These checks compile the installer and run offline tests; they do not launch
the installer or game.

## Release

Update the version in `packaging/starter-inputs.json` and `packaging/INSTALL.txt`,
then push to `main`. After tests and build pass, GitHub Actions tags and publishes
the new version as `q2jump-starter.exe`. Already-published versions are unchanged.

To upload an asset again for an existing release, run the [build workflow](.github/workflows/build.yml)
manually with its `release_tag`. Leave the tag empty for a build-only run.

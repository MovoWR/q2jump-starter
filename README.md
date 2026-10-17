# Q2JUMP Starter

Q2JUMP Starter is a Windows x64 installer for Q2PRO Race. It can set up a new
game folder or install and update Q2PRO Race in an existing Quake II folder.

[Download `q2jump-starter.exe` from Releases](https://github.com/MovoWR/q2jump-starter/releases)

The current version installs Q2PRO Race only.

## Install

1. Download and run `q2jump-starter.exe`.
2. Choose an installation option:

   | Option | Use it when | What Starter does |
   | --- | --- | --- |
   | **Install a new game** | You want a fresh folder | Downloads the pinned Quake II demo and official 3.20 update data, then installs the latest Q2PRO Race client. |
   | **Install or update Q2PRO Race in an existing Quake II folder** | You already have Quake II data | Reuses your existing PAK, shows the client files that will change, and installs or updates Q2PRO Race. |

3. Choose the game folder. The default for a new installation is `C:\Q2JUMP`.
   A new installation requires an empty folder unless Starter is retrying one of
   its own interrupted installations. For an existing game, select the Quake II
   root containing `baseq2\*.pak`.
4. Review the changes and finish the installation. You can clear the options to
   create a desktop shortcut or launch Q2PRO Race before selecting **Finish**.

Both options require an internet connection to download the current Windows x64
Q2PRO Race client. A new installation also downloads the original demo and 3.20
update archives. The installer supports 64-bit Windows 10 build 17763 or newer.
If the Microsoft Visual C++ x64 runtime is missing, Starter reports it so it can
be installed separately.

## What Starter keeps and verifies

- Existing custom PAKs, settings, startup scripts, and unrelated files are kept.
- Files that need replacing are backed up as part of a recoverable update.
- Downloaded archives are checked against their expected SHA-256 hashes before
  extraction. The original game installers are never executed.
- The Q2PRO Race download is checked against its GitHub asset digest and expected
  file inventory.
- Existing PAK contents are reused without being opened, decoded, or validated.
- The temporary installation helper is removed when setup finishes.

The Starter executable contains setup code and metadata. It does not bundle the
Q2PRO Race client, Quake II PAK files, or downloaded archives.

Starter does not create an uninstaller. Delete a Starter-created desktop shortcut
manually if you later remove the game folder. An existing desktop item named
`Q2PRO Race (Jump)` is left unchanged. Folders created by the older combined
Starter, identified by `Q2JUMP.installation`, are not adopted.

See [INSTALL.txt](packaging/INSTALL.txt) for the complete installation behavior.
The original Quake II data remains subject to the notices shown during setup.

## Build from source

Starter builds independently of the engine repository. Building requires:

- Windows
- Python 3.11 or newer
- PowerShell
- the .NET Framework x64 C# compiler
- the pinned inputs described in [SOURCE.txt](packaging/SOURCE.txt)

From the repository root, use fresh output directories:

```powershell
python -m unittest discover -s tests -p test_build.py -v
& ./client-helper/build.ps1 -Output build/client-helper-tests -Test
python scripts/build.py --output build/starter-next
```

GitHub Actions runs the offline tests and builds the installer for pushes and
pull requests to `main`, manual runs, and released GitHub releases. The workflow
stores and publishes only `q2jump-starter.exe`.

These checks compile the native components and exercise offline fixtures. They do
not run the installer or game, and they do not establish native wizard or in-game
behavior.

# Q2JUMP Starter

Standalone installer repository extracted from q2pro_race. This version still
installs Q2PRO Race only. R1Q2 Race and the choice to install either client or
both are planned follow-up work, not implemented here.

Starter is a Windows x64 setup program. The release file is named
`q2jump-starter.exe`; its internal product name remains Q2JUMP Starter.
The executable contains a temporary client installation helper and Starter
metadata, but no game client, Quake II PAK, or downloaded archive.

Starter offers two choices. **Install a new game** requires an empty folder (or
a Starter-owned folder being retried), downloads the pinned Quake II demo and
3.20 update archives listed in `docs/baseline-evidence/baseline-archives.json`,
checks their SHA-256 hashes, and installs the selected original data and
notices. **Install or update Q2PRO Race in an existing Quake II folder**
requires a PAK in `baseq2`, reuses that data without inspecting its
contents, and does not download the original data. Both choices download the
current Windows x64 client ZIP from `MovoWR/q2pro_race`'s `latest` GitHub release,
verify the GitHub asset digest and file inventory, and install the client and
Jump resources. When `latest` retains several Windows x64 ZIPs, Starter selects
the most recently uploaded one and leaves the older release assets intact.
The existing-game choice shows the files to add or replace
before applying a managed update; replacements have recovery backups. The
temporary helper is removed after setup.

The selected folder defaults to `C:\Q2JUMP`. Starter keeps existing custom
PAKs, settings, and unrelated files. After a successful setup, the finish
page offers checked options to create a desktop shortcut and launch Q2PRO Race
in Jump mode. An existing desktop item with the shortcut name is
left unchanged. Silent setup performs neither finish-page action. Starter
does not provide an uninstaller, so a created shortcut remains until removed
manually. A missing Visual C++ x64 runtime is reported rather than installed.
The original Quake II data remains subject to the
license shown during setup. See `packaging/INSTALL.txt` for the user-facing
installation details.

For an existing installation, select the Quake II root itself: the selected
folder must contain `baseq2` with a `.pak` file. The folder picker uses the
chosen folder directly and does not append `Q2JUMP`. Setup checks the folder
before showing the Ready to Install page.
An unrelated uninstaller in that folder is retained. Only the legacy combined
Starter's `Q2JUMP.installation` marker causes rejection as an older Starter install.

Starter builds independently of either engine repository. Build on Windows
with Python 3.11 or newer, PowerShell, the .NET Framework x64 C# compiler, and
the pinned inputs described in `packaging/SOURCE.txt`.

From the repository root, choose fresh output directories:

```powershell
python -m unittest discover -s tests -p test_build.py -v
& ./client-helper/build.ps1 -Output build/client-helper-tests -Test
python scripts/build.py --output build/starter-next
```

GitHub Actions runs tests and builds the installer on pushes and pull requests
to `main`, manual dispatch, and released GitHub releases. It stores only
`q2jump-starter.exe` as the workflow artifact and attaches that executable
to released GitHub releases. Generated output, downloaded archives, and vendor
tools are excluded from Git. Offline tests and compilation do not establish
native wizard behavior; the installer and game are not run by these checks.

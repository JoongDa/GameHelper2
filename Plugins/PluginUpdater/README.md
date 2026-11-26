# PluginUpdater

GameHelper2 port of [exApiTools/PluginUpdater](https://github.com/exApiTools/PluginUpdater).

Build the full `GameOverlay.sln` in Release. The build includes the manager in
`Plugins/PluginUpdater`, with its Git library, native dependency, catalog and
localization. Enable PluginUpdater in the host's Plugins page and open its settings.

- **Browse** reads the bundled GameHelper2 catalog. Settings accepts an HTTPS catalog
  URL or a local JSON file using the upstream `PluginDescriptions` format.
- **Add** clones a repository URL, including URLs ending in `/tree/<branch>`.
- **Manage** checks remote commits, refreshes local state, updates one or all
  repositories, switches branches, force-updates, reverts, recompiles and deletes.
  Force update and revert require confirmation. Open folder shows the source tree.
- **Settings** controls startup checks, periodic checks, log wrapping and automatic
  restart after successful compilation. Pending changes can also be applied using
  Restart and apply. Build errors stay in the console and can be retried with Build.

The .NET 10 SDK must be available on `PATH` to compile plugin source. Git operations
use bundled LibGit2Sharp. Configured Git credential helpers can supply credentials
for authenticated repositories when Git is installed.

Source clones and their `.git` metadata stay in `Plugins/PluginUpdater/Source`.
Builds use short, isolated temporary directories and reference the installed host
assemblies. Successful payloads are copied to `Staging` beside the manager.
Authors can use `$(GameHelperPath)` in their project references. Existing project
references to GameHelper and GameOffsets are adapted in the isolated build copy.
Repositories with multiple plugin projects can specify a relative `ProjectPath`
on their catalog fork entry. Optional `Branch` selects a branch for installation.

Successful builds are staged until restart. The helper waits for the host to exit,
backs up old plugin files, preserves `config` and `configs`, applies the requested
changes and starts `Launcher.exe`. The host's existing loader discovers the DLLs.
Plugin enable state remains in the host's `configs/plugins.json`. The restart
bridge uses the host's existing save-settings event through reflection.

Normal Git updates use pulls, including merges of diverged branches. Merge
conflicts are reported before compilation and can be resolved in the source
folder, or explicitly force-updated. Revert selects the parent source commit and
compiles it again. Deletion keeps a backup of the installed files and source
repository.

The bundled catalog is seeded from this repository's `plugin-sync.json` upstream
addresses. Build compatibility is checked against the installed host during
compilation. ExileAPI plugins use a different host API and need a GameHelper2 port
before they can be installed here.

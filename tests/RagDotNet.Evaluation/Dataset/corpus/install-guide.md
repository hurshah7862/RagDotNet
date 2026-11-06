# Installing Quill

Quill is a local-first note-taking app. Your notes live as plain Markdown files on your own disk, and Quill adds fast search, linking, sync and plugins on top. This guide covers system requirements, installing on each platform, the first launch, and how to upgrade or remove the app.

## System requirements

Quill runs on Windows, macOS and Linux. The desktop app needs about 400 MB of free disk space for the program itself, plus whatever space your notes take. A machine with 4 GB of memory is enough for vaults of up to roughly fifty thousand notes; larger vaults are fine but the first index build will take longer.

### Supported operating systems

- Windows 10 version 22H2 or later, 64-bit only.
- macOS 12 Monterey or later, on both Apple silicon and Intel Macs.
- Linux distributions with glibc 2.35 or newer, such as Ubuntu 22.04, Debian 12 and Fedora 38.

Older systems may start the app, but they are not tested and bug reports from them are closed without investigation.

### Graphics

Quill draws its editor with the GPU when one is available. On virtual machines or remote desktops without GPU acceleration, start Quill with the `--disable-gpu` flag, or set `render.gpu = false` in the configuration file.

## Installing on Windows

Download the `.msi` installer from the Quill website and run it. The installer puts the app under `C:\Program Files\Quill` and adds a Start menu entry. No administrator rights are needed if you choose "Install for me only", in which case the app goes under your user profile instead.

If you prefer a package manager, Quill is published to winget:

```powershell
winget install --id Quill.Quill --exact
```

Enterprise administrators can deploy the same `.msi` silently with `msiexec /i quill.msi /qn ALLUSERS=1`. Silent installs never open the welcome screen; each user sees it on their own first launch.

## Installing on macOS

Download the `.dmg` disk image, open it, and drag Quill into the Applications folder. The app is signed and notarized, so Gatekeeper opens it without warnings. Homebrew users can run:

```bash
brew install --cask quill
```

On first launch macOS asks whether Quill may access your Documents folder. Allow it if your vault lives there; otherwise you can pick a different folder and grant access only to that one.

## Installing on Linux

Quill ships three Linux packages. Choose the one that fits your distribution:

- A `.deb` package for Debian, Ubuntu and their derivatives.
- An `.rpm` package for Fedora, openSUSE and RHEL-compatible systems.
- A Flatpak on Flathub, which works on almost any distribution and updates through your software center.

To install the Debian package from a terminal:

```bash
sudo apt install ./quill_3.2.0_amd64.deb
```

The Flatpak build runs in a sandbox. It can only see your home folder by default, so if your vault is on a second drive, grant access with `flatpak override --user --filesystem=/mnt/notes app.quill.Quill`.

## First launch

The welcome screen asks you to create a vault or open an existing one. A vault is simply a folder: Quill keeps its own settings for that vault in a hidden `.quill` subfolder and never rewrites your Markdown files unless you edit them.

When you open a vault for the first time, Quill builds a search index in the background. A progress bar in the status bar shows how far along it is. You can keep writing while indexing runs; search results simply fill in as the index grows.

### Importing from other apps

The welcome screen also offers an importer. It reads exported folders of Markdown or HTML notes and converts internal links into Quill's `[[wiki link]]` style. Attachments are copied into an `attachments` folder inside the vault. The importer never deletes the source folder, so you can run it again if something goes wrong.

## Command-line tool

The installer also adds a small command-line tool named `quill`. It can open a vault, create a note from standard input, or run a sync without the window being open. Run `quill --help` to see every command. On macOS the tool is not added to your path automatically; choose **Install command-line tool** from the Quill menu to create the link in `/usr/local/bin`.

## Upgrading

Quill checks for updates once a day and downloads them in the background. When an update is ready, a small dot appears on the settings icon; restart the app to apply it. Updates never touch your notes. If you installed through winget, Homebrew, apt or Flatpak, the built-in updater is switched off and your package manager handles upgrades instead.

To stay on a specific version, turn off **Automatic updates** under Settings → General. You can still check manually from the same page.

## Uninstalling

Removing the app does not remove your vaults, because they are ordinary folders that you own. On Windows, uninstall from Settings → Apps. On macOS, drag Quill from Applications to the Trash. On Linux, remove the package with your package manager.

Quill also keeps an application data folder with logs, the plugin cache and window positions. Delete it as well if you want a completely clean removal:

| Platform | Application data folder |
|---|---|
| Windows | `%APPDATA%\Quill` |
| macOS | `~/Library/Application Support/Quill` |
| Linux | `~/.local/share/quill` |

Deleting the application data folder signs you out of Quill Cloud on that device but leaves your synced notes untouched on the server.

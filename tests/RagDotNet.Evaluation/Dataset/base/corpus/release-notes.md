# Release notes

This page lists what changed in each release of Quill, newest first. Releases follow semantic versioning: minor releases add features without breaking anything, and patch releases only fix bugs.

## Quill 3.2

Released in September.

### New features

- **Daily notes templates.** Daily notes can now start from a template. Pick one under Settings → Daily notes, and use placeholders such as the date and the weekday inside the template.
- **Faster search.** The search index was rewritten and now answers most queries in under twenty milliseconds, even on vaults with a hundred thousand notes. The first index build after upgrading takes a little longer than usual.
- **Profiles.** You can now keep several named sets of settings and switch between them from the account menu or with the `--profile` flag.
- **Folder sync** joins Quill Cloud and WebDAV as a third sync provider. It copies the vault to any folder, which makes it easy to use with other cloud drives.

### Improvements

- Pasting a table from a spreadsheet now produces a Markdown table instead of tab-separated text.
- The graph view groups notes by folder when the vault has more than two thousand notes, which keeps it readable.
- Tooltips on the status bar now explain every sync icon.

### Fixes

- Fixed a crash when renaming a folder that contained an open note on Linux.
- Fixed checklist items losing their ticked state when moved with Alt+Up.
- Fixed the importer creating empty notes for HTML files that contained only images.

## Quill 3.1

Released in June.

### New features

- **Vim mode.** Modal editing with Vim key bindings, switched on with the `editor.vim_mode` setting.
- **Attachment search.** Quill can now index the text of PDF attachments. Turn it on with `search.index_attachments`; it is off by default because it makes indexing slower.
- **Hardware security keys** are supported for two-factor sign-in to Quill Cloud, alongside authenticator apps.

### Improvements

- Conflict copies now include the device name in the file name, so it is easier to tell where each version came from.
- The plugin API gained `registerView` for adding sidebar panels.
- Daily snapshots are now compressed, which cuts their size on disk by about two thirds.

### Fixes

- Fixed sync stopping after the computer woke from sleep until the app was restarted.
- Fixed links to headings breaking when the heading contained a colon.

### Deprecations

The old `app.vault.read` plugin method is deprecated in favour of `app.notes.get`. It still works in 3.1 and 3.2 and will be removed in Quill 4.0.

## Quill 3.0

Released in February. This was a major release with changes that affect existing setups.

### Breaking changes

- **New configuration format.** Settings moved from `settings.json` to `quill.toml`. The first start of 3.0 converts the old file automatically and keeps a backup named `settings.json.bak`.
- **Plugin manifests require permissions.** Plugins written for 2.x must add a `permissions` list to `plugin.json` before they load. Until then they appear as disabled with an explanation.
- **Dropped 32-bit Windows.** Quill 3.0 and later are 64-bit only. The last version for 32-bit Windows is 2.9.4.

### New features

- **End-to-end encryption** for Quill Cloud vaults, with keys derived from a passphrase only you know.
- **Command palette** with fuzzy matching, opened with Ctrl+P.
- **Linux packages.** Official `.deb` and `.rpm` packages, plus a Flatpak on Flathub, replace the old AppImage.

### Improvements

- Startup is about twice as fast on large vaults because the index is loaded lazily.
- The editor now renders with the GPU when available, which makes scrolling smoother in long notes.

## Quill 2.9

Released the previous year. This was the last release of the 2.x line.

- **2.9.4** fixed a memory leak in the graph view that grew with every note opened.
- **2.9.3** fixed WebDAV sync failing on servers that return file names in a different Unicode normalization form.
- **2.9.0** added the importer for exported Markdown and HTML folders, and the ability to pin notes to the top of the sidebar.

## Support policy

The current minor release receives every fix. The previous minor release receives security fixes for six months after a new minor release comes out. Older versions are not supported, so we recommend leaving automatic updates on unless you have a specific reason to stay on one version.

## Upgrade notes

Upgrading never changes your notes. Before a major upgrade, it is still a good idea to make sure your vault is synced or backed up, because plugins written for an older major version may stop loading until their authors update them. Check the plugin settings page after upgrading; any plugin that failed to load shows the reason next to its name.

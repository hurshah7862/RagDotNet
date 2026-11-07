# Configuration reference

Most of Quill can be configured from the Settings window, but every setting is also stored in a plain text file that you can edit by hand, keep in version control, or copy between machines. This page explains where the file lives, how it is structured, and what each setting does.

## The configuration file

Quill reads its settings from a file named `quill.toml`. There are two of them, and they are merged when the app starts:

1. The **global file** applies to every vault on the machine. It lives in the application data folder, next to the logs.
2. The **vault file** lives at `.quill/quill.toml` inside a vault and applies only to that vault.

When a setting appears in both files, the vault file wins. This lets you keep a large font on your laptop while a shared team vault enforces its own sync rules.

### File format

The file uses TOML. Settings are grouped into tables such as `[editor]` and `[sync]`, and each line is a key and a value:

```toml
[editor]
font_size = 15
spell_check = true

[sync]
provider = "quill-cloud"
interval_minutes = 10
```

Unknown keys are ignored with a warning in the log, so a file written for a newer version still loads in an older one. Invalid values are a different matter: if a value has the wrong type, Quill shows a banner naming the key and falls back to the default for that one setting.

### Reloading

Changes made in the Settings window are written to the file immediately. Changes made by hand in a text editor are picked up within two seconds, because Quill watches both files for changes. There is no need to restart.

## Settings

The table below lists the settings most people change. Every key belongs to the table named before the dot.

| Key | Default | Description |
|---|---|---|
| `editor.font_size` | `14` | Editor text size in points, from 8 to 32 |
| `editor.line_width` | `80` | Maximum characters per line before soft wrapping |
| `editor.spell_check` | `true` | Underline misspelled words while typing |
| `editor.vim_mode` | `false` | Enable modal editing with Vim key bindings |
| `appearance.theme` | `"parchment"` | Colour theme; built-in themes are parchment, slate and midnight |
| `appearance.follow_system` | `true` | Switch between light and dark themes with the operating system |
| `sync.provider` | `"none"` | One of none, quill-cloud, webdav or folder |
| `sync.interval_minutes` | `5` | Minutes between automatic syncs when changes are waiting |
| `search.index_attachments` | `false` | Also index the text of PDF attachments for search |
| `backup.keep_snapshots` | `30` | How many daily snapshots of the vault to retain |
| `render.gpu` | `true` | Draw the editor with GPU acceleration |

### Editor

The editor settings control how notes look while you write. `editor.line_width` only affects wrapping on screen; it never inserts line breaks into the file. Vim mode supports normal, insert and visual modes, plus the most common motions and text objects. Ex commands other than `:w` and `:q` are not supported.

### Appearance

Themes change colours only, never layout. You can add your own theme by placing a CSS file in `.quill/themes` and selecting it by file name. When `appearance.follow_system` is true, Quill uses `parchment` in light mode and `midnight` in dark mode unless you pick different themes for each.

### Search

The search index lives in the application data folder, not inside the vault, so it is never synced. Turning on attachment indexing makes the first index build noticeably slower on vaults with many PDFs, and roughly doubles the size of the index on disk.

### Backups

Once a day, Quill takes a snapshot of the whole vault and stores it in compressed form in the application data folder. Snapshots older than the retention count are pruned automatically, oldest first. Snapshots are local only and are a safety net against accidental deletion, not a replacement for a real backup on another disk.

## Environment variables

A few settings can be overridden for a single session with environment variables, which is useful for testing or for running Quill from scripts:

- `QUILL_VAULT` opens the given folder as the vault, ignoring the last vault you used.
- `QUILL_LOG_LEVEL` sets the log level to `error`, `warn`, `info` or `debug`.
- `QUILL_CONFIG_DIR` moves the global configuration file and the application data folder somewhere else, for example onto a portable drive.

Environment variables take precedence over both configuration files.

## Profiles

If you use one machine for different kinds of work, you can create profiles. A profile is a named copy of the global settings. Switch profiles from the account menu or start Quill with `--profile writing`. Profiles share installed plugins but each has its own theme, font and keyboard shortcuts.

## Resetting

To return a setting to its default, delete its line from the file, or click the reset arrow next to it in the Settings window. To reset everything, close Quill and rename the global `quill.toml`; a fresh file with all defaults is written on the next start. Your notes are never affected by resetting settings.

# Troubleshooting

This page collects the problems people run into most often, with the quickest fix first. If none of these help, collect a diagnostic report as described at the end and contact support.

## Startup problems

### Quill opens a blank white window

A blank window almost always means the graphics driver cannot handle hardware rendering. Close Quill and start it once with `quill --disable-gpu`. If the window now draws correctly, make the change permanent by setting `render.gpu = false` in your global configuration file, and consider updating your graphics driver.

### Quill crashes immediately after the splash screen

Open the log file in the application data folder and look at the last lines. A message mentioning a plugin means a plugin failed to load; start Quill in safe mode by holding Shift while it launches, which skips all plugins, and then disable the plugin that crashed. A message about a locked database means another copy of Quill is already running, possibly hidden in the system tray.

### "Vault folder is not accessible"

The vault was moved, renamed, or is on a drive that is not connected. Reconnect the drive or open the vault from its new location using the vault switcher. On macOS, this message can also mean that Quill lost permission to the folder after a system update; grant it again under System Settings → Privacy & Security → Files and Folders.

## Sync problems

### Notes are not syncing

First look at the status bar. A spinning arrow means sync is running; a cloud with a slash means you are offline; a red exclamation mark means the last sync failed. Click the icon to see the error.

The most common cause of failed syncs is an expired sign-in. Sign out and back in under Settings → Sync. For WebDAV, check that your app password has not been revoked on the server.

### Sync is slow on the first run

The first sync uploads every note and attachment, which can take a while on large vaults. Later syncs send only what changed. You can watch progress in Settings → Sync, which shows how many files remain. Leaving the app open overnight is the simplest way to finish a big initial upload.

### I see files ending in "conflict copy"

These are created when the same note was edited on two devices before either synced. They are deliberate, not a bug. Open the original note, compare it with the copy using the banner at the top, and delete whichever version you do not need.

### Notes I deleted came back

This happens when a device that was offline for a long time still had the old notes and synced them back. Quill Cloud protects against it by remembering deletions for 60 days; devices offline for longer than that may resurrect deleted notes. Delete them again, and remove old devices you no longer use from your account page.

## Search problems

### Search does not find a note I know exists

The index may be out of date. Choose **Rebuild search index** from the Help menu; this deletes the index and builds it again from your files without touching the notes. Rebuilding a large vault can take several minutes, during which results are incomplete.

Also check that the note is not in an excluded folder. Folders listed under Settings → Search → Excluded folders are skipped entirely, and by default that list contains the `attachments` folder and any folder whose name starts with a dot.

### Search results show old text

Quill updates the index a moment after you stop typing. If results stay stale for more than a minute, the file watcher may have stopped, which can happen when the vault is on a network share. Restart Quill, or move the vault to a local disk for the best experience.

## Editor problems

### Typing feels laggy in long notes

Notes longer than about twenty thousand words can slow down live preview. Switch the note to source mode with the toggle in the top right corner, or split the note into several smaller notes linked together. Disabling spell check for that vault also helps noticeably.

### Pasted text loses its formatting

Quill converts pasted rich text into Markdown. Tables, bold, italics, links and lists survive the conversion; colours, fonts and text sizes do not, because Markdown has no way to express them. To paste text exactly as plain characters without any conversion, use Ctrl+Shift+V.

## Plugin problems

A misbehaving plugin can cause almost any symptom. The fastest way to find out is safe mode: if the problem disappears in safe mode, a plugin is responsible. Re-enable plugins one at a time until the problem returns. Plugin errors are also written to the log with the plugin's identifier at the start of the line.

## Collecting a diagnostic report

Choose **Create diagnostic report** from the Help menu. Quill writes a zip file to your desktop that contains the logs from the last seven days, your settings with any passwords and tokens removed, the list of installed plugins and their versions, and basic system information. The report never includes the content of your notes. Attach the zip file when you contact support at the address shown on the Help page.

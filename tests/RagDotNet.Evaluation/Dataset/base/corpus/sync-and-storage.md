# Sync and storage

Quill stores every note as a Markdown file in your vault folder. Sync is optional: without it, Quill is a fully offline app and your notes never leave the machine. This page explains how storage is laid out, which sync providers exist, how conflicts are handled, and what limits apply.

## How notes are stored

Each note is one `.md` file. Folders in the sidebar are real folders on disk, so you can reorganise notes with your file manager and Quill notices within a second. Note titles come from the first heading of the file, or from the file name when there is no heading.

### Attachments

Images, PDFs and other files you drop into a note are copied into the vault's `attachments` folder and linked with a relative path. Quill renames an attachment only when a file with the same name already exists, by adding a number to the end. Attachments larger than 200 MB are linked in place instead of copied, and a small chain icon marks them in the editor.

### Metadata

Quill keeps tags and links in the notes themselves, using front matter and wiki links, so no information is locked in a database. The hidden `.quill` folder holds only settings, themes, plugin data and the list of recently opened notes. Deleting it loses those preferences but none of your writing.

## Sync providers

Choose a provider under Settings → Sync or with the `sync.provider` key. Only one provider can be active per vault.

### Quill Cloud

Quill Cloud is the hosted service run by the Quill team. It is the only provider that supports end-to-end encryption, version history in the browser, and sharing a read-only link to a single note. The free plan includes 1 GB of storage across all vaults; paid plans start at 10 GB. Quill Cloud keeps deleted notes in a recycle bin for 60 days before removing them permanently.

### WebDAV

WebDAV works with any server that speaks the protocol, including Nextcloud and many network storage boxes. You enter a server address, a username and an app password. Quill uploads each note as its own file, so the server shows the same folder structure as your vault. WebDAV sync does not encrypt notes beyond what the HTTPS connection provides.

### Folder sync

Folder sync copies the vault to another folder, such as one managed by a third-party cloud drive. Quill treats that folder as the remote side and handles conflicts itself. Avoid pointing the vault and the target folder at the same drive sync client, because two programs reacting to each other's changes can create duplicates.

## When sync runs

Quill syncs when the app starts, when you press the sync button, and automatically on a timer while changes are waiting to upload. The timer interval is set by `sync.interval_minutes`. Remote changes are fetched every time a sync runs, and also immediately when Quill Cloud sends a push notification that another device changed something.

A sync never blocks typing. If you edit a note while it is uploading, the new edit is simply queued for the next round.

### Working offline

When there is no network connection, changes queue up locally and a cloud icon with a slash appears in the status bar. Nothing is lost; the queue is stored on disk and survives restarts. As soon as the connection returns, Quill uploads the queued changes in the order they were made.

## Conflicts

A conflict happens when the same note was changed on two devices before either change was synced. Quill first tries a line-based merge. If both devices changed different paragraphs, the merge succeeds silently and you see a small "merged" label in the note's history.

When both devices changed the same lines, Quill keeps your local version in place and saves the other version next to it as a conflict copy, named after the original note with the device name and date appended. A banner at the top of the note links to the copy so you can compare them side by side, keep one, or combine them by hand.

Conflict copies are ordinary notes. Delete them when you are done; they are not removed automatically.

## Version history

Every time a note is synced, the previous version is kept. With Quill Cloud you can browse history for any note from the note menu and restore an older version with one click. Restoring creates a new version, so restoring is itself undoable. With WebDAV and folder sync, history depends on what your server or drive provides.

## Limits

- A single note can be at most 25 MB of text; larger files are left out of sync and a warning lists them.
- File and folder names cannot contain characters that Windows forbids, such as `?` or `:`, because a vault must work on every platform. Quill offers to rename such files when it finds them.
- A vault can contain up to 500,000 files. Beyond that, sync still works but the initial scan becomes slow.

## Moving a vault

To move a vault to another folder or disk, close Quill, move the folder, and open it again from the vault switcher. Sync settings travel with the vault because they live in `.quill/quill.toml`, so the vault reconnects to the same provider without signing in again.

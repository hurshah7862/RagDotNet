# Migration guide

This guide explains how to move an existing collection of notes into Quill from another app, and how to tidy up what the importer brings across.

## Importing notes from other apps

Most people who start using Quill already have years of notes somewhere else. The importer is built to bring those notes across in one pass, keeping as much of their structure as the source app allows. It runs entirely on your own machine: nothing you import is uploaded anywhere, and the importer never needs your password for the other app. Instead, you export your notes from the old app using its own export feature, and then point Quill at the exported file or folder. This keeps the process predictable, because the export format is documented by each vendor and does not change when they update their servers.

Before you begin, make sure the vault you are importing into has enough free disk space. The importer writes every note as a Markdown file and copies attachments next to it, so the vault grows by roughly the size of the export plus a small amount for metadata. It is also a good idea to import into a new, empty vault the first time. That way you can look at the result, decide whether you like how it was converted, and throw it away without affecting anything else if you want to try again with different settings. Once you are happy, you can either keep using the new vault or move the imported folder into your main one.

To start an import, open the File menu, choose Import, and pick the source app from the list. Quill then asks for the exported file or folder and shows a preview of what it found: the number of notes, the number of attachments, the tags or labels it recognised, and any items it will not be able to convert. Nothing is written until you confirm the preview. The table below lists the sources the importer understands and what each one keeps.

| Source | What to export | What carries over |
|---|---|---|
| Evernote | An ENEX file for each notebook | Notes, tags, attachments and creation dates |
| Notion | The Markdown and CSV export, as a zip file | Pages, nested pages as folders, databases as tables |
| Apple Notes | A folder written by the Quill helper for macOS | Notes and folders, without locked notes |
| OneNote | Section exports made with the importer add-on | Pages, with section groups as folders |
| Bear | A Bear backup file | Notes, tags and the pinned state of each note |
| Markdown folder | Any folder of Markdown files | Everything, with links rewritten to Quill's style |

Each source has its quirks. Evernote stores formatting as a limited form of HTML, which the importer turns into Markdown headings, lists and tables; unusual styling, such as coloured text or custom fonts, is dropped because Markdown has no way to express it. Notion exports nest pages inside folders named after their parent page, and the importer keeps that nesting, so a deep workspace becomes a deep folder tree. If you prefer a flatter layout, you can choose to flatten pages below a certain depth in the preview step. Apple Notes does not offer a general export, which is why Quill ships a small helper app for macOS that reads your notes with your permission and writes them to a folder in a format the importer understands.

Tags deserve a moment of attention. Evernote and Bear both have tags, and they come across as Quill tags with the same names. Notion has no tags as such, but multi-select properties on a database are turned into tags on the pages of that database. OneNote has neither, so imported OneNote pages arrive untagged. In every case the preview lists the tags it will create, and you can rename or merge them before the import runs, which is much easier than cleaning them up afterwards across hundreds of notes.

Dates are preserved where the source records them. Creation and modification dates from Evernote and Bear are written into each note's front matter and also set on the file itself, so sorting by date in Quill matches the order you saw in the old app. Notion only exports the last edit time, and Markdown folders only have whatever dates the files already carry on disk. If a source provides no date at all, the importer uses the time of the import and marks the note so you can find these notes later with a search for imported notes without a source date.

Attachments are copied into an attachments folder beside each imported note, and links in the note are rewritten to point at the copies. Images keep their original file names unless two attachments in the same note share a name, in which case a number is added to the second one. Very large attachments are copied too, but the preview warns you about any file over a hundred megabytes so that you are not surprised by how much space the vault takes. Attachments the source app stored only as links to the web are left as links, because the importer does not download anything from the internet on your behalf.

The importer is careful about text encoding. Modern exports are almost always UTF-8, and those are read as they are. Older exports, particularly from Markdown folders that have been copied between computers for many years, sometimes contain files saved in a legacy encoding. Files that are not valid UTF-8 are read as Windows-1252 and converted, which covers the most common case of accented letters in older Western European notes. The conversion is listed in the import report for every file it applied to, so you can check those notes by eye if the result looks wrong.

Large imports can take a while. The importer processes notes in batches and shows its progress as it goes, and you can keep using Quill in another vault while it runs. If something interrupts it, such as the computer going to sleep, the app being closed, or the disk filling up, the work done so far is not lost. When you start the same import again, Quill recognises the export and offers to continue: the importer resumes from the last note it finished writing, skipping everything already in the vault. You can also choose to start from scratch instead, in which case it first removes the notes from the unfinished attempt.

## Cleaning up after an import

Even a careful import leaves some tidying to do. Notes written over many years in another app tend to contain links that pointed at that app's own pages, tags that were created on the spur of the moment and never used again, and attachments that no longer matter. This section walks through the cleanup steps that most people find worthwhile, in the order that usually works best. None of them are required, and you can come back to them at any time; Quill works perfectly well with an untidy vault.

Start with the import report. At the end of every import, Quill writes a note called Import report into the root of the vault. It lists what was imported, what was skipped and why, every file whose encoding was converted, every link that could not be resolved, and every attachment that was larger than the warning limit. The report is an ordinary note, so you can search it, link to it, and tick items off as you deal with them. Keep it until you have finished cleaning up; it is the quickest way back to the notes that need attention.

Broken links are the most common problem. When a note in the old app linked to another note, the export usually contains a reference in that app's own format, such as an internal identifier or a web address on the vendor's site. The importer rewrites the references it can match to the right Quill note, but some cannot be matched during the import itself, for example when the target note was in a notebook you did not export, or when two notes share the same title. Those links are left as they were and listed in the report.

Tags often need consolidating. Over the years most of us create near-duplicates: a tag in the singular and the same tag in the plural, a tag with a typo, or two tags that meant the same thing at different times. Open the tag pane, sort it by the number of notes, and look at the long tail of tags used only once or twice. Dragging one tag onto another merges them, updating every note that used the first tag. Merging is recorded in the undo history for the session, so if you merge the wrong pair you can take it back straight away.

Folder structure is worth a look as well. Some sources, Notion in particular, produce deep folder trees, and others put everything into a single folder. Quill does not mind either way, because search and links work across the whole vault, but a structure you understand makes browsing easier. Moving notes between folders in Quill keeps every link to them working, since Quill updates links when a note moves. You can therefore reorganise freely without worrying that you will break the connections between notes.

Attachments can take up a surprising amount of space. The attachments pane, available from the View menu, lists every attachment in the vault with its size and the notes that use it. Sort it by size to find the largest files, and look for attachments no note links to any more, which can happen when an old note was edited in the source app but the export kept the original attachment. Quill can move unused attachments to the system trash in one step, which gives you a chance to recover them if you change your mind.

The command line tool is handy for the links that the importer could not resolve during the import. Once the whole collection is in the vault, more of those links can usually be matched, because the notes they point at have now arrived too. Run the link fixer from a terminal, first in preview mode to see what it would change:

```
quill import fix-links --dry-run --vault ~/Notes
quill import fix-links --vault ~/Notes
```

The first command prints each link it would rewrite, with the note it was found in and the note it now points at, and changes nothing. The second command applies the same changes. Links that still cannot be matched stay as they are and are added to a new section at the end of the import report, so the report always reflects what is left to do. If two notes share a title and the fixer cannot tell which one a link meant, it leaves the link alone rather than guessing.

Sometimes the right answer is to undo the import entirely, perhaps because you chose the wrong export file or you want different settings for tags and folders. Quill makes this safe. Before writing anything, every import takes a snapshot of the vault, and the import can be rolled back from the Import history window in the File menu. Rolling back removes every note and attachment the import created and restores any note it changed, while keeping notes you created or edited yourself since then. The snapshot taken before the import is kept for 14 days, after which it is deleted to free the space it uses.

Finally, give the vault a little time. It is tempting to clean up everything in one evening, but most people find it easier to fix things as they come across them in daily use. The import report, the tag pane and the attachments pane are always there when you want them, and nothing about an imported note is different from a note you write in Quill yourself.

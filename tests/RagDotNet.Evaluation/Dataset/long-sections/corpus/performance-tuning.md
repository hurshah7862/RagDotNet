# Performance tuning

Quill is quick with a few thousand notes on any recent computer, and most people never need to change anything. If your vault has grown very large, if you keep big attachments in it, or if you use an older machine, the settings described here can make a noticeable difference. This page explains how the search index and the cache work and which settings are worth changing.

## The search index

Search in Quill is fast because it does not read your notes when you search. Instead, Quill keeps an index: a compact file that records which words appear in which notes and where. When you type in the search bar, Quill looks the words up in the index and only opens the notes that match, which is why a search over tens of thousands of notes still returns results as you type. The index lives in a hidden folder inside the vault, so it moves with the vault, but it is never synced between devices, because each device can build its own index more cheaply than it could download one.

The index is kept up to date as you work. Every time you save a note, Quill updates the index entries for that note in the background, which takes a few milliseconds for a typical note. Notes changed by sync, or by another program editing the files in the vault directly, are picked up the same way as soon as Quill notices the files have changed. You never need to rebuild the index during normal use, and Quill does so on its own after an update that changes the index format.

Building the index from scratch happens when you open a vault for the first time on a device, or after the index has been deleted. For a vault of a few thousand notes this takes a few seconds. For very large vaults it can take several minutes, and Quill shows its progress in the status bar. You can use the vault while the index is being built; notes that have not been indexed yet are still found by title, and full-text results fill in as indexing proceeds. Search results are marked as incomplete until the build has finished.

What goes into the index can be adjusted. By default Quill indexes the full text of every note, the names of attachments, and the text inside PDF attachments. Indexing PDFs makes their contents searchable but takes extra time and space, so if you keep many large PDFs that you never need to search, you can turn PDF indexing off in the search settings. Folders can be excluded from the index altogether, which is useful for folders of reference material or exported data that would only clutter your search results with matches you rarely want.

Languages matter for search. Quill splits text into words and reduces them to a common form, so that a search for running also finds run and runs. The rules for doing that depend on the language, and Quill uses the language set for each note, or the vault's default language if a note has none. If your notes mix languages, setting the language on the notes that differ from the default improves search results noticeably, particularly for languages with many word endings. Changing the default language rebuilds the index in the background.

Very large files are treated differently. A note of a few hundred kilobytes is unusual but fine, and is indexed like any other. Some vaults contain much larger notes, often pasted logs, exported chat histories or data dumps, and indexing their full text would slow down every search for little benefit. For that reason, notes larger than 5 MB are indexed by title only, and a small notice at the top of such a note says so. You can still open and search inside the note with the find bar, which reads the note directly instead of using the index.

The index takes some space on disk. As a rough guide, it uses about a tenth of the space taken up by the notes themselves, not counting attachments, plus more if PDF indexing is on. On a device with little free space, excluding folders you never search is the most effective way to keep the index small, followed by turning off PDF indexing. The storage page in the settings shows how much space the index uses in each vault, so you can see the effect of a change.

If search seems to be missing notes, or returns results that do not contain the words you searched for, the index may be damaged, for example after the disk ran out of space while it was being written. Rebuilding it fixes this. You can rebuild from the search settings, or from a terminal with the command line tool, which lets you choose how many threads to use so that a rebuild on a powerful machine finishes sooner:

```
quill index status --vault ~/Notes
quill index rebuild --threads 4 --vault ~/Notes
```

The status command reports the number of notes indexed, the size of the index, and when it was last rebuilt, and it warns if the index and the vault disagree. The rebuild command deletes the index and builds it again from the notes. Quill does not need to be closed while it runs; any open window simply shows search results as incomplete until the rebuild finishes.

## Large vaults and the cache

Quill keeps recently used notes ready in memory so that switching between them is instant. This cache holds each note's text, its rendered preview, its links and backlinks, and thumbnails of its images. When you open a note that is already in the cache, Quill shows it immediately; when it is not, Quill reads the file from disk and renders it, which takes longer for notes with many images or large tables. The cache is the main lever for making Quill feel faster or for making it use less memory, and the right balance depends on how much memory your computer has and how you use your notes.

For most people the default settings work well. They are chosen to suit a computer with eight gigabytes of memory and a vault of up to about twenty thousand notes. If you have more memory and switch between many notes during the day, increasing the cache size keeps more of them ready. If you have less memory, or you run many other demanding programs at the same time, reducing the cache size leaves more memory for them, at the cost of a short delay the first time you open each note.

The settings that affect performance most are on the Performance page of the settings. Each one can be changed while Quill is running, and the change takes effect immediately, so you can try different values and see what difference they make. The table below lists them with their defaults.

| Setting | Default | What it controls |
|---|---|---|
| Cache size | 512 MB | Memory kept for recently opened notes before the oldest are dropped |
| Image thumbnails | On | Small previews of images, made in the background |
| Live backlink counts | On | The number of links to each note, shown in the sidebar |
| Preview rendering | On | Rendering tables, diagrams and maths in the editor as you type |
| Background indexing | Normal | How much processor time the search index may use |

Image thumbnails are made once and stored on disk, so they cost time only when an image is first added. If your vault contains thousands of large photographs, the first thumbnails can take a while to generate after you open the vault on a new device, and turning thumbnails off makes the sidebar and the attachments pane show file names instead. Preview rendering is worth turning off on a slow machine if you edit long notes full of tables or diagrams, because rendering them again on every keystroke can make typing feel sluggish.

Backlinks are one of Quill's most useful features, and also one of the most expensive to keep up to date. Every time a note changes, Quill works out which other notes it links to and updates their backlink lists, and the sidebar shows a count next to each note. In a typical vault this is instant. In a vault with a very large number of notes and links, recounting can use noticeable processor time, especially while sync is bringing in many changes at once. Vaults with more than 100,000 notes should turn off live backlink counts, which stops the sidebar counts from updating continuously; the backlinks panel of each note still works and is computed when you open it.

Sync also affects performance. When sync brings in a large batch of changes, for example after a device has been offline for weeks, Quill has to update the index and the cache for every changed note. It does this in the background at low priority, so the app stays responsive, but you may notice the fans on a laptop for a while. Background indexing can be set to Low in the settings to make this gentler at the cost of taking longer, or to High if you prefer it to finish as quickly as possible.

Attachments are best kept in moderation. Quill copes with large attachments, but every attachment has to be synced, thumbnailed, and, for PDFs, indexed. If you keep large media such as videos or disk images in your vault only for reference, consider storing them elsewhere and linking to them instead. A link to a file outside the vault opens the file with its usual app, and it does not slow down sync or take up space in the vault.

Several vaults open at once each keep their own cache and index, so memory use grows with every open vault. If you only need an old vault now and then, close its window when you are done rather than leaving it open in the background. Quill remembers the vaults you have used recently, so reopening one later takes a single click from the vault switcher, and its cache fills up again as you work.

Finally, if Quill still feels slow after adjusting these settings, the diagnostics page in the help menu can record a short performance trace while you reproduce the problem. The trace stays on your computer unless you choose to send it with a support request, and it shows which part of Quill was busy, which usually points to the setting that will help.

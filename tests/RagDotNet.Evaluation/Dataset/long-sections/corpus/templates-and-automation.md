# Templates and automation

Many notes start the same way: a meeting with an agenda and actions, a daily journal entry, a book with its author and a rating. Templates save you from typing that structure every time, and rules let Quill create or tidy notes for you on a schedule. This page covers both.

## Templates and variables

A template is an ordinary note whose content is copied into a new note when you use it. You can write templates in exactly the same way as any other note, with headings, lists, tables, checkboxes and links. The only thing that makes a note a template is where it lives: Quill treats every note in the vault's Templates folder as a template and offers it in the template picker. Keeping templates as normal notes means you can edit them with all the usual tools, link to them, and sync them to every device along with the rest of the vault.

To create a note from a template, open the command palette and choose New note from template, or press the shortcut you have assigned to that command. The picker lists every template by its title, with the most recently used at the top. Choosing one creates a new note in the current folder with the template's content already filled in. You can also insert a template into an existing note at the cursor, which is handy for adding a standard section, such as a list of action items, to a note you have already started writing.

Templates become far more useful with variables. A variable is a name in double curly braces that Quill replaces with a value at the moment the template is used. The simplest example is the date: a journal template that starts with the date variable produces a note headed with today's date, whatever day you create it. Variables can appear anywhere in a template, including the title, headings, tables and front matter, and a template can use the same variable as many times as it likes.

Some variables ask you for a value. When a template contains a prompt variable, Quill shows a small form before creating the note, with one field for each prompt, and fills in what you type. A book template might prompt for the title and the author, while a meeting template might prompt for the people attending. The form remembers the last value you entered for each prompt, which saves typing when you create several similar notes in a row, and you can clear a remembered value by deleting it in the form.

Dates and times can be formatted. By default the date variable uses the date format chosen in the editor settings, but you can add a format after a colon to override it for one template, such as a year followed by the week number for a weekly review. Formats follow the familiar letter codes used by most calendar software, and the help panel in the template editor shows a list of them with examples. Dates can also be offset, so a template can refer to tomorrow or to the start of next week, which is useful for planning notes.

Front matter in templates is merged rather than copied blindly. If a template has front matter with tags or other properties, the new note receives them, and variables inside the front matter are replaced like anywhere else. When a template is inserted into an existing note, its front matter is combined with the note's own: tags are added to the existing ones, and other properties are only set if the note does not already have them. This means inserting a template never overwrites information you had already entered.

Templates can build on each other. Instead of copying the same block of text into several templates, put it in a template of its own and include it with the include variable, giving the title of the template to include. A team might keep one template for the standard footer of every meeting note and include it from the templates for each kind of meeting. When the shared template changes, every template that includes it picks up the change the next time it is used. Notes already created are not affected, because a template only matters at the moment a note is created from it.

The table below lists the built-in variables. Variable names are not case-sensitive, and an unknown variable is left in the note as it was written, so a typo is easy to spot.

| Variable | Replaced with |
|---|---|
| date | Today's date, in the format set in the editor settings |
| time | The current time, in 24-hour form |
| title | The title typed when the note was created |
| cursor | The place where typing continues after the template is inserted |
| clipboard | The text currently on the clipboard |
| include | The content of another template, given by its title |

A few habits make templates easier to live with. Give templates clear titles that say what they are for, because the picker shows nothing else. Keep them short: a template that tries to cover every possible case ends up with sections that are deleted more often than they are used. And review your templates every few months, deleting the ones you no longer use, so the picker stays quick to scan. If you want to keep an old template without seeing it in the picker, move it to a subfolder named Archive inside the Templates folder; Quill skips that subfolder when it lists templates.

Templates are shared like any other note. If the Templates folder is inside a shared notebook, every member of that notebook sees the same templates in their picker, which is a convenient way for a team to agree on the structure of its meeting notes or reports. Members with the editor role can change the shared templates, and the change reaches everyone at the next sync.

## Scheduled rules

Rules let Quill do routine work on your behalf. A rule has a trigger, which says when it runs, and an action, which says what it does. The most common use is to create notes on a schedule, such as a daily journal page every morning or a weekly review every Friday afternoon. Rules can also tidy up, for example by moving notes with a certain tag into an archive folder once they are old enough, or by adding a tag to every note created in a particular folder.

Rules are written as small text files in the vault's Rules folder, one rule per file. Keeping them as files means they sync to every device and can be shared with a team, just like templates. Quill reads the Rules folder when the vault opens and whenever a file in it changes, so editing a rule takes effect straight away. A rule file uses a simple format of names and values, one per line, and the rule editor in the settings checks the file as you type and points out any mistakes before you save it.

The trigger of a rule is usually a schedule. Schedules are written in plain English, such as every day at 07:30, every weekday at 09:00, or the first day of every month. Times are in your computer's local time zone, and Quill adjusts automatically when the clocks change. A rule can also be triggered by an event instead of a schedule, such as a note being created in a folder or a tag being added to a note, which is how the tidying rules described below work.

The action of a rule says what to do when it runs. Creating a note from a template is the most common action, and it uses the same templates described above, so variables such as the date are filled in at the moment the rule runs. Other actions move notes, add or remove tags, or append a line to an existing note, which is useful for keeping a running log. A rule that would do nothing, for example because the note it should create already exists, simply skips that run and records the fact in the rule log.

Here is a complete rule that creates a weekly review note every Friday afternoon from a template, placing it in a Reviews folder:

```
rule: weekly-review
schedule: every Friday at 16:00
action: create-note
template: Weekly review
folder: Reviews
title: Week {{date:YYYY-[W]WW}}
```

The title line uses the date variable with a format, so each review note is named after the week it covers. Because the note's title is different every week, the rule never collides with an earlier review, and you build up a folder with one note for each week of the year.

Rules that act on existing notes need a filter to say which notes they apply to. A filter can match a folder, a tag, the age of a note, or text in its title, and filters can be combined. A tidying rule might move every note tagged done that has not been edited for thirty days into an Archive folder. Filters use the same syntax as Quill's search, so you can try a filter in the search bar first and see exactly which notes it would match before you put it into a rule.

Quill keeps a log of every rule run. Open it from the Rules page in the settings to see when each rule last ran, what it did, and any problems it met, such as a missing template or a folder that no longer exists. A rule that fails three times in a row is paused automatically and marked with a warning, so a broken rule does not keep failing in the background without you noticing. Fixing the rule file and saving it resumes the rule.

Scheduled rules run on whichever of your devices has Quill open at the time. If several devices are open, they agree among themselves which one runs the rule, so each run happens once. If no device has Quill open when a rule is due, the rule waits: rules that miss their time while Quill is closed run once at the next start, rather than once for every missed occasion. A daily journal rule therefore creates one note when you open Quill after a week away, not seven. If you would rather have the missed notes too, set the rule's catch-up option, and Quill creates one note for each missed day instead.

Rules run with the same permissions as you. In a shared notebook, a rule can only do what your role allows, so a commenter's rule cannot move or edit notes. Rules belong to the person who wrote them, even when the Rules folder is shared, which means a rule written by one member does not run on the devices of the others unless they choose to enable it in their own settings.

# Keyboard shortcuts

Quill is designed to be used without a mouse. Nearly every action has a shortcut, and those that do not can be reached from the command palette. This page lists the default shortcuts and explains how to change them.

## Conventions

On macOS, use the Command key wherever this page says Ctrl, and Option wherever it says Alt. Shortcuts written with a comma, such as `Ctrl+K, Ctrl+S`, are chords: press the first combination, release it, then press the second.

## The command palette

Press `Ctrl+P` to open the command palette. Start typing to filter the list of commands; the palette matches words in any order and forgives small typos. Each entry shows its shortcut on the right, which makes the palette a good way to learn shortcuts over time. Plugins add their own commands to the palette as well.

## Default shortcuts

### Navigation

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Quick open a note by typing part of its title |
| `Ctrl+Shift+F` | Search the full text of every note in the vault |
| `Ctrl+Tab` | Cycle through recently viewed notes |
| `Alt+Left` | Go back to the previously viewed note |
| `Ctrl+B` | Show or hide the sidebar with the folder tree |
| `Ctrl+G` | Open the graph view of linked notes |

### Editing

| Shortcut | Action |
|---|---|
| `Ctrl+N` | Create a new note in the current folder |
| `Ctrl+Shift+D` | Insert the date of today at the cursor |
| `Ctrl+L` | Turn the current line into a checklist item |
| `Ctrl+Enter` | Tick or untick the checklist item under the cursor |
| `Ctrl+]` | Indent the current list item one level deeper |
| `Ctrl+K` | Insert a link around the selected text |
| `Alt+Up` | Move the current line or selection up by one line |

### Formatting

| Shortcut | Action |
|---|---|
| `Ctrl+I` | Italic |
| `Ctrl+Shift+B` | Bold |
| `Ctrl+Shift+X` | Strikethrough |
| `Ctrl+1` to `Ctrl+6` | Turn the line into a heading of that level |
| `Ctrl+Shift+C` | Wrap the selection in an inline code span |

Bold uses `Ctrl+Shift+B` rather than the more familiar `Ctrl+B`, because `Ctrl+B` was taken by the sidebar before formatting shortcuts were added. You can swap them if you prefer; see below.

### Windows and panes

| Shortcut | Action |
|---|---|
| `Ctrl+\` | Split the editor and open the note side by side |
| `Ctrl+W` | Close the current tab |
| `Ctrl+Shift+T` | Reopen the tab you closed most recently |
| `F11` | Enter or leave distraction-free full screen |
| `Ctrl+,` | Open the Settings window |

## Changing shortcuts

Open Settings → Keyboard to see every command with its current shortcut. Click a shortcut and press the new key combination to change it. If the combination is already used by another command, Quill shows which one and asks whether to reassign it.

Shortcuts are saved in a file named `keymap.json` inside the active profile, so each profile can have its own layout. The file is a list of objects with a `command` and a `keys` field. Editing it by hand is supported, and changes apply as soon as you save the file.

```json
[
  { "command": "format.bold", "keys": "Ctrl+B" },
  { "command": "view.toggle-sidebar", "keys": "Ctrl+Shift+E" }
]
```

### Removing a shortcut

To remove a shortcut without assigning a new one, right-click it in the keyboard settings and choose **Remove**. In `keymap.json`, set `keys` to an empty string. The command stays available from the command palette.

### Resetting shortcuts

The **Reset all** button at the bottom of the keyboard settings restores the defaults for the active profile. It does not affect other profiles or any other settings.

## Vim mode

When `editor.vim_mode` is on, the editor starts in normal mode and the usual Vim motions apply. Quill's own shortcuts that use Ctrl still work in every mode, so `Ctrl+P` opens the palette even in normal mode. Press `Esc` twice quickly to leave insert mode and close any open popup at the same time.

## Global shortcut

Quill can register one system-wide shortcut that works even when the app is in the background. By default it is not set. Assign it under Settings → Keyboard → Global quick capture. Pressing it opens a small floating window where you can type a quick note; when you press `Ctrl+Enter` there, the note is saved to the inbox folder of your default vault and the window disappears.

## Accessibility

All shortcuts can also be triggered through screen readers and switch-access devices, because every command is exposed through the command palette. The keyboard settings include a search box, so you can find a command by name and hear its current binding read aloud.

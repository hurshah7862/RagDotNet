# Collaboration

Quill is a personal notes app first, but many people also keep notes with others: a household sharing recipes and plans, a small team keeping its handbook, or a class working on a shared reading list. This page explains how sharing works, who can do what, and how comments and history help a group keep track of changes.

## Shared notebooks and roles

Sharing in Quill happens at the level of a notebook. A shared notebook is a folder in your vault that is also synchronised to every person you invite, through the same sync service that keeps your own devices up to date. Everything inside that folder, including subfolders, notes and attachments, is shared with the members of the notebook, and nothing outside it is. This makes the boundary easy to see: if a note is in the shared folder, its members can see it, and if you move it out, they no longer can. Quill shows a small people icon on shared folders in the sidebar so that you always know which notes other people can read.

To share a folder, right-click it in the sidebar and choose Share. Quill asks you to confirm that you want the folder and everything in it to become visible to others, then opens the sharing panel. From there you can invite people by email address or create an invitation link. People who already use Quill see the invitation in their notifications; people who do not are asked to create a free account before they can join. Either way, the shared notebook appears in their sidebar as soon as they accept, and its contents start to download in the background.

Every member of a shared notebook has a role, which decides what they can do. The person who shared the folder starts as its owner, and they choose a role for each person they invite. Roles can be changed later from the sharing panel by the owner, and the change takes effect on the member's devices the next time they sync, usually within a few seconds. The roles are deliberately few, so that it is always clear who is able to change what.

Choosing a role is mostly about how much you trust someone with the content and how much you want them involved. For a household notebook, everyone is usually an editor. For a team handbook, a few people might be editors while the rest of the team are commenters, so that suggestions come in as comments that the editors can act on. For material you want to publish to a wider group without discussion, the viewer role keeps things simple. You can mix roles freely within one notebook, and there is no limit on how many members have each role.

Ownership can be handed over. An owner who is leaving a team, for example, can make another member the owner from the sharing panel. The new owner receives a notification and must accept before the change takes effect, so ownership never moves to someone who did not expect it. The previous owner becomes an editor and can then leave the notebook if they wish. A notebook always has exactly one owner, which is why the owner cannot leave without first passing ownership on to someone else.

Members can leave a shared notebook at any time from its context menu. Leaving removes the notebook from all of their devices, but it does not delete anything for anyone else. If they had made changes, those changes stay in the notebook, attributed to them in the history. An owner can also remove a member, with the same effect for that person. In both cases, notes the person copied out of the shared folder into their private vault stay with them, because Quill cannot reach into a private vault, which is worth bearing in mind before sharing anything sensitive.

The table below summarises what each role allows. It applies to every note and attachment in the notebook, including subfolders, since roles cannot be set on individual notes.

| Role | What the member can do |
|---|---|
| Owner | Everything, including changing roles and deleting the notebook |
| Editor | Create, edit, move and delete notes, but not change members |
| Commenter | Read every note and leave comments, without changing the text |
| Viewer | Read every note, with no comments and no edits |

Invitations are designed to be safe to send. An invitation by email can only be accepted by someone signed in with that address, so forwarding the email to someone else does not let them join. An invitation link is more convenient, especially for groups, but anyone who has the link can use it, so it is best shared through a channel you trust. For that reason, an invitation link stops working after seven days, and the owner can revoke it earlier from the sharing panel. Each link also carries the role it was created with, so a link made for viewers cannot be used to join as an editor.

There are some limits on the size of a shared notebook, mostly to keep sync fast for everyone involved. A shared notebook can have up to 50 members, counting the owner, and you can be a member of as many shared notebooks as you like. The notebook's size counts towards the storage of its owner, not of its other members, which means joining a large notebook does not use up your own storage allowance. If the owner runs out of storage, the notebook becomes read-only for everyone until space is freed or the owner's plan is upgraded.

Finally, encryption works with sharing too. If the owner's vault uses end-to-end encryption, the shared notebook does as well, and each member's devices receive the notebook key through their own encrypted channel. Members who join later can read the whole history of the notebook, but someone who has been removed cannot read changes made after their removal, because the notebook key is replaced when a member leaves or is removed.

## Comments, mentions and history

When several people work on the same notes, it helps to talk about the text without changing it. Comments in Quill are attached to a passage of a note: select some text, choose Comment from the context menu, and write your remark. The passage is highlighted for everyone in the notebook, and the comment appears in the margin next to it. Anyone with the commenter, editor or owner role can comment, and anyone who can read the note can see the comments. Comments sync like the rest of the notebook, so a remark made on one person's phone appears on everyone else's devices a few moments later.

Comments are threaded. Replying to a comment adds to the same thread rather than starting a new one, so a discussion about one sentence stays together. Each thread shows who wrote each reply and when, and the author of a reply can edit or delete it afterwards. Edits are marked as edited, so that the conversation stays honest about what was said. Long threads collapse to show the first comment and the most recent reply, and can be expanded with a click when you want to see everything that was said.

Mentions bring a person into a conversation. Typing the at sign followed by a member's name, in a comment or in the text of a note, notifies that person. They see a notification on all of their devices, and clicking it opens the note at the place where they were mentioned. Mentions only work for members of the notebook, so you cannot accidentally notify someone who has no access to the note. Each member chooses in their settings whether mentions also send an email, which is useful for people who do not open Quill every day.

Edits by different people are merged automatically in most cases. If two members change different parts of a note at the same time, both changes are kept. If they change the same sentence, Quill keeps both versions side by side and marks the conflict, so that one of them can choose which to keep. The person who resolves the conflict is recorded in the history, together with what they chose. In practice conflicts are rare in shared notebooks, because members usually see each other's cursors while they type and naturally avoid working on the same line.

Presence makes collaboration feel less like passing files around. When another member has the same note open, their initials appear at the top of the note, and their cursor is shown in their colour as they type. You can click their initials to jump to where they are working. Presence is only shown while both of you are online; if one of you is offline, changes are exchanged when the connection returns and merged in the usual way.

Resolving a comment marks the discussion as finished. Any member who can comment can resolve a thread, and the passage loses its highlight once that happens. Resolved comments are hidden from the margin but remain in the note's history, so nothing said in a discussion is ever lost. To see them again, open the comment menu at the top of the margin and choose Show resolved. A resolved thread can be reopened by replying to it, which brings back the highlight for everyone.

The history of a note records every saved change, who made it, and when. Open it from the clock icon at the top of the note to see a timeline of versions. Selecting a version shows the note as it was at that moment, with the differences from the current version highlighted. Any editor or owner can restore an older version, which creates a new version on top of the history rather than erasing what came after, so restoring is itself something that can be undone.

History is kept for a long time, but not forever. In shared notebooks, version history keeps every saved change for 90 days, after which older versions are thinned out to one version per day for the following year. This keeps the history useful for recovering recent mistakes while stopping it from growing without limit for notes that are edited constantly. Comments and their replies are kept for as long as the note exists, regardless of the age of the versions around them.

Notifications can be gathered into a digest for busy notebooks. Instead of a separate notification for every comment and mention, each member can choose to receive one summary a day listing the threads that changed and the places where they were mentioned. The digest arrives at a time the member picks, and items in it link straight to the passage concerned. Mentions marked as urgent by their author still arrive immediately, whatever the digest setting.

Deleted notes in a shared notebook go to the notebook's own trash rather than to anyone's personal trash. Any editor or owner can restore them from there, and the trash shows who deleted each note and when. Notes stay in the shared trash for thirty days before they are removed for good, which gives the group time to notice if something was deleted by mistake.

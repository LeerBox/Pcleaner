# Safety and cleanup scope

PCleaner is designed around preview-first cleanup. It builds an explicit list of targets, displays the expected effect, validates every target again immediately before deletion, and records the result in the Activity log.

## Default selection

- **Safe** rules cover regenerable caches and temporary data and are selected by **Recommended**.
- **Side effects** cover actions such as emptying the Recycle Bin or clearing saved sessions and start unselected.
- **Privacy** rules cover cookies, history, website identifiers, local storage, and recent-file lists and start unselected.

Selections are remembered per rule. Always run **Analyze** after changing a selection and review the preview before **Clean now**.

## Protected data

The safety guard rejects paths that escape a rule’s cleanup root, network paths, protected Windows and user folders, protected browser directories, and protected user-data files. Browser cleanup never targets extensions, extension state, passwords, bookmarks, sync data, account databases, preferences, or browser settings. History cleanup uses an allow-list of known stores and values rather than deleting whole registry branches.

PCleaner does not erase information stored in online accounts and does not block advertisements or network requests. It cleans local files and databases only.

## Application locks and elevation

Locked data is skipped unless the selected settings allow a safe close. A normal close request is used first. Automatic closing is available only in administrator mode and is limited to windowless background processes; visible applications are not forcibly terminated. Closed applications can be reopened after cleanup when that setting is enabled.

Administrator mode may be needed for system-wide temporary files, protected cleanup handlers, reboot scheduling, and automatic closing. Use **Restart as administrator** only when the preview shows items that require it.

## Irreversible effects

Cleaning deletes local data. Cookies and website storage can sign you out, history cleanup removes local history, and recent-file cleanup can remove saved sessions or unsaved editor tabs. Save work, close applications, and keep backups of anything important before confirming.

## Scope checklist

Before selecting **Clean now**, confirm:

1. The selected categories and individual rules are expected.
2. The preview does not contain a document, project, download, password, bookmark, extension, or other data you need.
3. Browsers and editors are closed when their data is selected.
4. You understand any sign-out, session, or recent-file effects shown in the rule details.

See [Implementation references](REFERENCES.md) for the sources behind individual cleanup rules. The references describe formats and boundaries; they are not a guarantee of compatibility with every application version.

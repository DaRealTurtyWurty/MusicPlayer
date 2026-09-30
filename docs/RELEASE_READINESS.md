# Release readiness plan

1. **Documentation:** root README, source build commands, portable installation, import/play, upgrade, uninstall, diagnostics and recovery instructions.
2. **Build and CI:** pinned SDK, committed dependency locks, deterministic compilation, Windows regression job, self-contained x64 package and SHA-256 manifests.
3. **Reliability:** bounded diagnostics, startup failures visible without database reset, SQLite online snapshots before migration and at startup, manual backup, validated offline restore and preservation of replaced files.
4. **Accessibility:** audit existing keyboard actions and modal focus; fill accessible-name and focus gaps; test critical UI automation names and constrained layouts. WPF uses device-independent sizes and layout rounding.
5. **Acceptance:** run the full offline regression runner, dedicated recovery/accessibility checks and release publish. Complete the manual matrix below before labeling a package production-ready.

## Manual release gate

Use a fresh Windows x64 VM/user profile without a separately installed .NET runtime. Automated checks do not replace these hardware, assistive-technology and real DPI checks.

| Check | Expected result |
| --- | --- |
| Extract package as standard user; launch | All native/runtime dependencies load; empty library opens |
| Import MP3/FLAC/WAV, folder and relative M3U8; play, pause, seek | Metadata and playlists appear; sound reaches chosen device |
| Restart after playlist/queue edits | Library, playlist order and session persist |
| Upgrade a previous package with populated library | Pre-upgrade snapshot exists; library and playlists retained |
| Close app; preserve data; damage test database; restore snapshot | Failure is visible; backup restores songs, playlists and session; damaged files archived |
| Invalid snapshot; restore while another instance runs | Restore rejected; existing library retained |
| Keyboard only through all pages and dialogs | Visible focus, reachable controls; Enter, Escape, context menu and queue reorder work |
| Narrator on navigation, track lists, queue, seek, output and dialogs | Meaningful names and current control states announced |
| Windows scale 100%, 125%, 150%, 200%; move between monitors | Text sharp, no overlapping controls; scrolling reaches Settings; popups usable |
| Minimum window size and 1920×1080 screen at 200% | Navigation and playback controls usable; Settings can scroll |

The minimum main window is 900×560 device-independent units. Displays with less usable logical space may require a lower scale or larger resolution. True per-monitor DPI transitions and Narrator speech must be checked interactively.

## Audit findings

Existing playback buttons, volume/seek sliders, page navigation and import/confirmation panels already have explicit accessible names. Library and browser Enter actions, playlist Enter/Delete, queue Enter/Delete/Alt+arrows, modal Escape and Cancel-first focus already exist. Custom button, slider, text-box and list-row templates have focus indicators. Added a focus visual for the custom output ComboBox, names for library/browser lists, track rows, watched folders and queue rows, and backup feedback as a polite live region. Settings wraps text and scrolls vertically. A dedicated regression checks critical names, keyboard bindings and layout at logical sizes corresponding to common scaling factors; it does not simulate Narrator speech or physical monitor DPI changes.

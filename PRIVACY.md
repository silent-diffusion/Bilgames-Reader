# Bilgames Reader privacy policy

_Last updated: 9 November 2026_

Bilgames Reader turns EPUB books into full-cast audiobooks on your own devices. This policy covers the
app for iPhone, iPad and Mac, and the app for Windows. In short: **we collect nothing.** There are no
accounts, no analytics, no ads, no tracking and no crash reports, and the developer has no server that
receives anything from the app.

## iPhone, iPad and Mac

### What stays on your device

Your books, their text, the speakers the app finds, your voice choices, the audio it generates,
your bookmarks, highlights and notes, and your listening positions are stored inside the app on your
device. They leave it only if you turn on iCloud sync (below), or when you yourself export a backup
or an audiobook to a place you choose.

### The voice, and the assistant, run on your device

- **Voice.** The narration is made by a voice model that runs on your device. The app sends no book
  text anywhere to make audio.
- **Assistant.** The optional assistant that helps decide who is speaking uses **Apple Intelligence
  on your device** (Apple's on-device model). It sees a few paragraphs around the line it is asked
  about, the book's title and its main characters' names. Nothing goes to a server, and what it says
  is kept only in the book's own files.

### The one download: the voice model, from GitHub

The first time you generate audio, the app downloads the voice model and its voices (about 800 MB,
once per device) from **GitHub** (`github.com`, `raw.githubusercontent.com` and GitHub's download
servers), where the open-source model is published. Like any download, GitHub sees your IP address
and the time. The app sends no cookies, no account and nothing about you or your books. It doesn't
go online for anything else: there are no update checks, ads or analytics.

### iCloud sync (optional, off by default)

If you turn on **Settings › iCloud › Sync with iCloud**, your library goes to **your own private
iCloud**, in Bilgames Reader's own area of your iCloud account. Only your devices signed in to your
Apple Account can read it. The developer can't see it, and Apple stores it under its own
[privacy policy](https://www.apple.com/legal/privacy/).

What syncs:

- in **iCloud Drive** (the app's own folder, not shown in the Files app): each book's EPUB, cover,
  text, speakers, voices, notes, bookmarks, highlights, listening position and the finished audio;
- in **iCloud's key-value storage** (small and fast): the latest listening position and speed,
  bookmarks and highlights, and which device is generating which chapter.

Your devices name themselves in these records by **kind and a random code**, such as "Mac 3F2A" or
"iPhone 91C0", never by your device's own name. (Test builds before November 2026 used the device's
name; those records are replaced the next time each device writes them, and deleting everything from
iCloud removes them.)

The key-value storage needs only your iCloud account, not iCloud Drive. So if you switch off iCloud
Drive for Bilgames Reader in your device's settings, the files stop syncing but listening positions
and notes still sync while the app's own **Sync with iCloud** switch is on. The switch inside the app
is the one that stops all syncing.

### Lock screen and Now Playing

While a book plays, its chapter, title, author (or the speaker) and cover show where your device
shows what's playing: the lock screen, Control Centre, the Mac menu bar, a paired Apple Watch,
CarPlay and AirPlay receivers. This is the same as any audio app and is handled by your device; the
app sends it nowhere else.

### Your device's own backups

Like other app data, the library is part of your device's iCloud Backup or computer backup if you use
one. That backup is Apple's and yours; the developer has no access to it.

### How to delete your data

- **One book:** in the library, delete the book. It is removed from this device and, if it was in
  iCloud, from iCloud and your other devices that sync, **whether or not sync is on here**. It
  doesn't come back when you turn sync on again.
- **From iCloud only:** **Settings › iCloud › iCloud Storage** lists the books in iCloud. Delete one
  there, or choose **Delete Everything from iCloud…** to remove all your books, audio, notes and
  positions from iCloud. That turns sync off; the books on your devices stay where they are, and
  your other devices stop syncing instead of deleting their copies. A tiny marker with a random code
  and a date stays, so other devices know what happened. Deleting can't be undone.
- **Everything on a device:** delete the app. On iPhone and iPad that removes all its data; on a Mac,
  also delete `~/Library/Containers/io.github.silent-diffusion.BilgamesReader`.
- **If you can't open the app:** your device's iCloud settings (Manage Storage › Bilgames Reader)
  can delete the app's iCloud Drive data.

## Windows

- Everything (your books, their text, speakers, voices, audio, bookmarks, highlights, notes, settings
  and diagnostic logs) stays on your PC in `%LOCALAPPDATA%\BilgamesReader`. The logs never contain
  book text and are never sent anywhere.
- The app goes online **only** when you ask it to: downloading a voice model (from GitHub),
  downloading the optional review assistant (its model from Hugging Face, `huggingface.co`, and
  llama.cpp from GitHub), or checking for and installing updates (from this project's GitHub
  releases; checking at start-up is off unless you turn it on). Like any download, those sites see
  your IP address and the time; the app sends nothing about you or your books.
- The review assistant runs **on your PC**. Its server listens only on your own computer, with a key
  made for each session, and sends nothing out.
- To delete everything, uninstall the app and delete the folder above.

## Children

The app collects no data from anyone, children included.

## Changes and contact

If this policy changes, the new version will be published here with a new date. Questions: open an
issue at [github.com/silent-diffusion/Bilgames-Reader](https://github.com/silent-diffusion/Bilgames-Reader/issues).

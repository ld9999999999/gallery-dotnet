# Gallery

Gallery is a self-hosted photo, video and audio gallery. Many of us don't have time to organise the
photos we copy to our computers or NAS, so Gallery gives you a clean place to browse and manage them
even when your on-disk folders are messy. Your original files are untouched - Gallery keeps references
and metadata in its own SQLite database, with thumbnails and converted videos in a separate data folder.

Gallery runs as a web server for private sharing, or as a desktop app (on Windows, macOS and Linux).

https://github.com/user-attachments/assets/8730740f-b5eb-47cb-8957-3aa7f2a5b730


**Features:**

- Scans for photos, videos and audio files in the folders you specify.
- Monitors the folders for files added, moved or deleted.
- Shows one item per photo or video to combine duplicates and file types - such
  as JPG, RAW and resized ones. The other files become "variations".
- Sort by the date it was taken, and builds albums from your folders.
- Create thumbnails, supports most video formats (via ffmpeg), and interact with 360° photos.
- Face detection in photos.
- Edit RAW files and JPEGs with the most common adjustments, with JPEG export.
- Assemble collages from up to 9 photos.

Web server versus Desktop app:

| | Web server (service) | Desktop app |
| --- | --- | --- |
| Where | Any web browser, on any device on your network | Desktop app window |
| Best for | A home server or NAS that runs all the time | One computer, used by one person |
| Users | An `admin` account and a `viewer` role | Just you |
| Project | `src/Gallery.Web` | `src/Gallery.Desktop` |

## Contents

- [Build requirements](#build-requirements)
- [Build](#build)
- [Run it as a service (web server)](#run-it-as-a-service-web-server)
- [HTTPS](#https)
- [Run it as a desktop app](#run-it-as-a-desktop-app)
- [Using Gallery](#using-gallery)
- [Settings](#settings)
- [Good to know](#good-to-know)

## Build requirements

Needed to build and run:

- **.NET 10 SDK**, from <https://dotnet.microsoft.com/download>.

Optional. Gallery runs without them, with some limits:

| Tool | What it is for | Without it |
| --- | --- | --- |
| `ffmpeg` and `ffprobe` | Video and audio details, video thumbnails, and converting videos a browser can't play | Photos work; video and audio don't |
| `heif-dec` and `heif-info` (libheif) | HEIC photos from iPhones | ffmpeg reads them instead, but not always well |
| `dcraw_emu` and `raw-identify` (LibRaw) | Editing RAW files | RAW files show using their embedded JPEG, but can't be edited |
| `wasm-tools` .NET workload | Building the photo editor that runs in the web browser | No browser photo editor |

Default tool usage is from your `PATH` environment variable. If they live elsewhere, provide the full
path in the settings or on the command line, for example `--Gallery:FfmpegPath=/opt/ffmpeg/bin/ffmpeg`.
Run the web server with `--help` to see all options.

**HEIC without `libheif`.** iPhones store HEIC photos as a grid of tiles. Only ffmpeg 7.1 or later can
join the tiles, and some recent iPhone files won't open in ffmpeg at all, so install libheif
to avoid incompatibility. If you install it later, Gallery will reread your HEIC photos on the
next start.

Face finding uses [InsightFace](https://github.com/deepinsight/insightface) models, which live outside
this repository. Turn faces on in **Admin → Faces** and Gallery will download them. The models are
free for private use only.

### Install the tools

**macOS** (with [Homebrew](https://brew.sh)):

```bash
brew install --cask dotnet-sdk
brew install ffmpeg libheif libraw
```

**Linux** (Debian and Ubuntu; other distributions have the same packages under similar names):

```bash
# .NET 10 SDK: follow https://learn.microsoft.com/dotnet/core/install/linux for your distribution
sudo apt install ffmpeg libheif-examples libheif-plugin-libde265 libraw-bin
```

**Windows** (in PowerShell):

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Gyan.FFmpeg
```

On Windows, libheif and LibRaw have no simple installer. If you want them, download the programs and set
their paths with `Gallery:HeifDecPath`, `Gallery:HeifInfoPath`, `Gallery:DcrawEmuPath` and
`Gallery:RawIdentifyPath`.

The browser photo editor is built as a WASM application. You will need to install `wasm-tools` to
build it (`sudo` might be needed on macOS and Linux if dotnet is not in your home directory):

```bash
dotnet workload install wasm-tools
```

## Build

```bash
dotnet build Gallery.slnx
```

The first build downloads packages and takes a few minutes. Without the `wasm-tools` workload, add
`-p:BuildEditor=false` to skip the browser photo editor:

```bash
dotnet build Gallery.slnx -p:BuildEditor=false
```

### Make a build you can copy to another machine

`dotnet publish` creates a folder with everything the program needs. Set `-r` to the
system the program will run on: `osx-arm64` (Apple silicon Mac), `osx-x64` (Intel Mac), `linux-x64`,
`linux-arm64` or `win-x64`.

```bash
dotnet publish src/Gallery.Web     -c Release -r linux-x64 --self-contained false -o publish/web
dotnet publish src/Gallery.Desktop -c Release -r linux-x64 --self-contained false -o publish/desktop
```

If you don't want the target machine to have to install the .NET **runtime**, then build with
`--self-contained true`. The built binary folder will contain everything needed to run.
Always set `-r`, otherwise the build will include the face-finding library for every system, which
can take lot of disk space.

## Run it as a service (web server)

Connect to the web server from your browser. It suits a home server that your family has
access to.

### Try it first

From the source folder:

```bash
dotnet run --project src/Gallery.Web -- \
  --Gallery:DataDir=~/.gallery \
  --Gallery:Sources:0=/path/to/photos \
  --Gallery:Sources:1=/path/to/videos \
  --Gallery:InitialAdminPassword=change-me
```

On Windows PowerShell, use Windows paths, for example `--Gallery:Sources:0=D:\Photos`.

Then open <http://localhost:5080> (or <https://localhost:5443>) in a browser and sign in as `admin` with
that password. Without `InitialAdminPassword`, Gallery will generate a random password, prints it in
the log and saves it in `admin-initial-password.txt` in the data folder.

The first scan will automatically start. Add more folders later in **Admin → Sources & scan**.

Notes:

- `DataDir` holds the database (`gallery.db`), thumbnails (`thumbs/`) and converted videos and edits
  (`derived/`). You can move `derived` in **Admin → Settings**.
- The server listens on port 5080 (HTTP) and 5443 (HTTPS) on **all** network addresses, so anyone on your
  network can open it. Set an admin password and, in **Admin → Settings**, a viewer password. Until
  there is a viewer password, anyone can view without signing in.
- To change the ports, use `--Urls="http://0.0.0.0:8080;https://0.0.0.0:8443"`. Drop the `https://`
  address to serve plain HTTP only, or use `127.0.0.1` instead of `0.0.0.0` to accept only your own
  machine.
- Any option can go in `appsettings.json` (under `Gallery`) or in environment variables
  (`Gallery__DataDir`, `Gallery__Sources__0`, and so on) instead of the command line.
- A scan interrupted by a server stop continues at the next start. A scan you cancel in the admin page
  stays cancelled.

### Keep it running

After `dotnet publish`, copy the `publish/web` folder to the machine and start it without `dotnet run`:

```bash
./Gallery.Web --Gallery:DataDir=/var/lib/gallery --Gallery:Sources:0=/srv/photos
```

(`Gallery.Web.exe` on Windows.) If it can't find .NET, set the `DOTNET_ROOT` environment variable to the
folder where .NET is installed.

To run as a service, use your system's own tool. Examples below are a starting point for each OS.

**Linux (systemd).** Save as `/etc/systemd/system/gallery.service`:

```ini
[Unit]
Description=Gallery photo server
After=network.target

[Service]
User=gallery
WorkingDirectory=/opt/gallery
ExecStart=/opt/gallery/Gallery.Web --Gallery:DataDir=/var/lib/gallery --Gallery:Sources:0=/srv/photos
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

Then run:

```bash
sudo useradd --system --create-home --home-dir /var/lib/gallery gallery
sudo systemctl daemon-reload
sudo systemctl enable --now gallery
journalctl -u gallery -f        # watch the log; the admin password is here if you did not set one
```

The `gallery` user must be able to read your photo folders. With a very large library, Linux can run out
of file watches. The log will explain how to raise the limit
(`sudo sysctl fs.inotify.max_user_watches=524288`). Until then, Gallery still finds deleted files at
each start and scan, just not immediately.

**macOS (launchd).** Save as `~/Library/LaunchAgents/gallery.plist`. It starts when you log in:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>gallery</string>
  <key>ProgramArguments</key>
  <array>
    <string>/Users/you/gallery/Gallery.Web</string>
    <string>--Gallery:DataDir=/Users/you/.gallery</string>
    <string>--Gallery:Sources:0=/Users/you/Pictures</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>/tmp/gallery.log</string>
  <key>StandardErrorPath</key><string>/tmp/gallery.log</string>
</dict>
</plist>
```

Then run `launchctl load ~/Library/LaunchAgents/gallery.plist`. macOS may ask you to allow access to
folders such as Documents or an external drive. `launchctl unload` with the same path stops it.

**Windows.** Gallery isn't a Windows Service, so use Task Scheduler to start it with the machine. In an
administrator Command Prompt:

```bat
schtasks /Create /TN "Gallery" /SC ONSTART /RU SYSTEM /TR "C:\Gallery\Gallery.Web.exe --Gallery:DataDir=C:\GalleryData --Gallery:Sources:0=D:\Photos"
schtasks /Run /TN "Gallery"
```

A wrapper such as [NSSM](https://nssm.cc) can also run it as a real service. Windows may ask you to
allow the program through the firewall on first run.

Ensure that you only run one server per data folder.

## HTTPS

The web server serves HTTPS on port 5443 alongside HTTP on port 5080. Some features need it, such as
turning a 360° photo by moving your phone (iOS and Android).

### The built-in certificate

Without a certificate of your own, Gallery creates a self-signed one on first start and saves it as
`gallery.crt` and `gallery.key` in `{DataDir}/https`.

- It covers `localhost`, the machine's name (and `name.local`) and the machine's IP addresses.
- It lasts 800 days. Gallery replaces it 30 days before it ends, or when it no longer covers the
  machine's name.
- The log shows its names and SHA-256 fingerprint.

Browsers warn about self-signed certificates. To stop the warning, trust `gallery.crt` on each device:

- **macOS:** open `gallery.crt` in Keychain Access, add it to the System keychain, then set it to
  **Always Trust**.
- **Windows:** double-click `gallery.crt`, choose **Install Certificate**, and put it in **Trusted Root
  Certification Authorities**.
- **Linux (Debian/Ubuntu):** `sudo cp gallery.crt /usr/local/share/ca-certificates/ && sudo update-ca-certificates`.
  Firefox has its own list: add it in its certificate settings.
- **iPhone and iPad:** send the file to the device and install it, then turn it on in
  **Settings → General → About → Certificate Trust Settings**.
- **Android:** **Settings → Security → Encryption & credentials → Install a certificate → CA certificate**.

To open Gallery by a name the certificate doesn't cover, such as the DNS name `photos.example.com`, add
the name and restart. Gallery makes a new certificate:

```bash
./Gallery.Web --Gallery:Https:Hosts:0=photos.example.com --Gallery:Https:Hosts:1=192.168.1.20
```

Trust the new certificate again afterwards.

### Use your own certificate

To use a certificate from Let's Encrypt or any other authority, specify them in the settings:

```bash
./Gallery.Web --Gallery:Https:CertificatePath=/etc/letsencrypt/live/photos.example.com/fullchain.pem \
              --Gallery:Https:KeyPath=/etc/letsencrypt/live/photos.example.com/privkey.pem
```

Or a `.pfx` / `.p12` file with its password:

```bash
./Gallery.Web --Gallery:Https:CertificatePath=/path/to/gallery.pfx --Gallery:Https:Password=secret
```

The same in `appsettings.json`:

```json
{
  "Gallery": {
    "Https": {
      "CertificatePath": "/etc/letsencrypt/live/photos.example.com/fullchain.pem",
      "KeyPath": "/etc/letsencrypt/live/photos.example.com/privkey.pem",
      "RedirectHttp": true
    }
  }
}
```

| Setting (`Gallery:Https:…`) | Meaning |
| --- | --- |
| `CertificatePath` | A PEM certificate (a full-chain file sends its intermediate certificates too), or a `.pfx` / `.p12` file |
| `KeyPath` | The PEM private key, when it is not in the certificate file |
| `Password` | The password of an encrypted PEM key or of the `.pfx` file |
| `Hosts:0`, `Hosts:1`, … | Extra names or IP addresses the built-in certificate must cover |
| `RedirectHttp` | `true` sends `http://` requests to the HTTPS address |

Notes:

- The user that runs Gallery must be able to read the key file. With Let's Encrypt on Linux, the
  `gallery` user usually can't read `/etc/letsencrypt/live`. Give it access, or copy the files somewhere
  it can read after each renewal.
- Gallery reads the certificate at start, so **restart it after you renew**. The log warns 14 days before
  the certificate ends. With certbot, a deploy hook does this, for example
  `certbot renew --deploy-hook "systemctl restart gallery"`.
- For the standard port 443, use `--Urls="http://0.0.0.0:80;https://0.0.0.0:443"`. On Linux, ports below
  1024 need extra rights, so it is often simpler to keep 5443 and forward 443 to it in your router.

## Run it as a desktop app

From the source folder:

```bash
dotnet run --project src/Gallery.Desktop
```

Or, after `dotnet publish`, start `Gallery.Desktop` (`Gallery.Desktop.exe` on Windows) from the
`publish/desktop` folder.

On first start, open the **Settings** tab, add your source folders and press **Scan now**. The **All
photos**, **Albums**, **Years** and **People** tabs show the result. Click a photo to open the viewer, and
press `E` there to edit it.

The **Settings** tab has:

- **Source folders**: add, untick to pause, or remove. A new folder is scanned straight away.
- **Scan**: scan now, cancel, and generate missing thumbnails.
- **Folder for transcodes and edits**: empty means `{library}/derived`.
- **Library folder** and the **ffmpeg / ffprobe** paths. **Apply and reopen library** opens the library in
  the new folder, creating it if needed.
- **Faces**: turn face finding on and download the models.

Notes:

- The library (database and thumbnails) is in `~/.gallery` by default.
- The app keeps its own settings (library folder, ffmpeg paths, thumbnail size and zoom) in a file
  called `desktop.json`: `~/Library/Application Support/Gallery` on macOS, `~/.local/share/Gallery` on
  Linux and `%LOCALAPPDATA%\Gallery` on Windows.
- Videos and audio open in your default player. **Show in folder** reveals the file on disk.
- **Don't run the web server and the desktop app on the same library at once.** Each runs its own
  scanner, so close one before starting the other.

## Using Gallery

Most of this works the same in the browser and the desktop app.

### Browse

- Filter by text, kind (photo, video, audio), tag, person, "duplicates only" or "variations only", and
  change the sort order.
- A year shows its items a month at a time. The **▲ ▼** buttons jump between months.
- The sidebar shows library totals (items, disk use, duplicates and the space they waste, missing
  files) and what Gallery is doing now: scan, thumbnails and video conversion. Click the duplicates or
  variations figure to see those items.
- In the desktop app, Page Up, Page Down, Home and End scroll the grid.

### The viewer

Click an item to open it.

| Key | Action |
| --- | --- |
| `←` `→` | Previous / next item |
| `I` | Show the info panel (desktop; on the web, use the info button) |
| `Z` | Switch between fit and 100% |
| `+` `−` `0` | Zoom in, zoom out, fit. ⌘/Ctrl + scroll and double-click also zoom |
| `[` `]` | Turn the photo a quarter turn |
| `F` | Show face boxes (web) |
| `V` | Show a 360° photo flat |
| `E` | Edit the photo |
| `Esc` | Close |

- The info panel shows the camera, lens, exposure settings, date and a map link for the location. **All
  metadata** lists every EXIF, GPS, XMP and IPTC tag. Videos show their format and streams.
- The info panel also lists every variation of the item, with **Open** and **Download** links, and
  **Make primary** to choose which file the gallery shows.
- An admin's rotation is saved for everyone. A viewer's applies to their own screen only.

### Dates

Gallery reads the date a photo was taken from its EXIF data, or from the video or audio file. Without
one, it uses a date at the start of the file name, such as `20201001 beach.jpg` or
`2020-10-01_party.mp4`, and failing that, the file's own time. Future dates (for example from a camera
clock set wrong) are ignored.

### Albums

- Gallery creates one album for each folder. Folders with generic names such as `DCIM`, `100CANON`,
  `RAW` or a year count as the folder above them, so `Japan/DCIM/100CANON` and `Japan/RAW` both go into
  **Japan**. Two folders with the same name are joined when their dates overlap.
- To choose an album's cover, open a photo from it and use **Use as cover of …** in the info panel.
- **Admin → Albums → Regenerate auto-albums** rebuilds the folder albums, for example after dates
  change. Your own albums are untouched, but photos you added by hand to a folder album are removed.
- **Admin → Albums** also lists empty albums and removes them in one go.

### Tags and selection

- Select items with click, Shift-click for a range, or **Select all (filtered)**.
- Add your own tags (a key and a value) to the selection. Tags work as filters.
- Gallery adds some tags itself, such as `person: Name` for named people.

### People (faces)

- Turn faces on in **Admin → Faces** (desktop: **Settings → Faces**) and download a model pack:
  - `buffalo_l` (191 MB): accurate detection and is the default.
  - `buffalo_s` (16 MB): for slow or low-power computers.
- Face detection is done in the background.
- To fix mistakes, open a person and use the **Faces** tab: **Not this person** or **Move to…**. You can
  also rename, merge and hide people, and choose their cover.
- **Faces nobody is assigned to** lists the remaining faces.
- In the viewer, press `F` to see boxes over faces, and click a box to name it.
- Viewers see only named people, and only in photos they are allowed to see.
- Face finding runs on the processor, or with CoreML on a Mac, and uses about 430 MB of memory while running.

### Hide items from viewers

An admin can hide items (from the selection bar or the info panel) or whole albums (from the album's
banner). Hidden items disappear for viewers everywhere, while admins still see them, dimmed and marked.

### Video and audio

- Most MP4, MOV, WebM, MP3, WAV, FLAC and AAC files play directly in the browser. H.265 (HEVC) plays in
  Safari, and in Chrome and Edge when the computer can decode it.
- Other formats (MKV, AVI, MTS, WMV, WMA ...) are converted by `ffmpeg` as they play, to H.264 up to 1080p.
- With **Keep transcoded copies** on (**Admin → Settings**), the converted copy will be saved.
- **Convert while idle** (**Admin → Settings**, off by default) converts these videos in the background
  once nobody has used Gallery for 5 minutes, using only spare processor time. **Admin → Transcodes**
  shows its progress.

### 360° photos

- 360° photos open in a view you can look around in. Drag to turn, scroll or pinch to zoom, and
  double-click to return to the start.
- Gallery detects them from metadata. Insta360, RICOH THETA, GoPro MAX and Fusion, Samsung Gear 360 and
  others work.
- On a phone, a button turns the view as you move the phone. On iOS this needs HTTPS.
- If Gallery gets it wrong, an admin can mark a photo as 360° or flat in the info panel.

### Anamorphic lenses

An anamorphic lens squeezes the picture. An admin can set the lens's factor (for example 1.33× or 2×)
on a photo or video in the info panel, or on a selection with **Anamorphic…**.

### Photo editor

Press `E` or the **Edit** button in the viewer (web: admin only). The editor works on RAW,
JPEG and HEIC photos. It saves your settings alongside the original.

- **Basic:** Temperature, Tint, Exposure, Contrast, Brightness, Highlights, Shadows.
- **Colour:** Saturation, Vibrance.
- **Curves:** RGB, R, G and B. Drag to add or move a point; double-click to remove it.
- **Detail:** noise reduction and sharpening. Check these at 100%.
- **Crop and straighten** (`C`): drag the frame or its edges, pick a ratio, and use the Straighten
  slider to level the horizon. Press Enter or Esc when done.

| Key | Action |
| --- | --- |
| Hold `\` | Show the photo before your changes |
| ⌘/Ctrl + `Z` | Undo (the last 4 changes) |
| ⌘/Ctrl + `S` | Save |
| `Z` or double-click | 100% view |
| ⌘/Ctrl + `+` / `−` / `0` | Zoom in, zoom out, whole picture |
| `C` | Crop and straighten |
| `Esc` | Leave the editor |

**Recipes** are saved sets of settings. Save one with **Save as recipe…**, and choose which sections it
holds, for example only the colour. Apply it in the editor, or to many photos at once with **Apply
recipe…** in the selection bar. Undo is available for 10 seconds afterwards.

### Collages

Select up to 9 photos and press **Collage…** in the selection bar (web). Gallery lays them out without
cropping, and **Rearrange** shows other layouts. Set the width, the gap between photos, the background
colour and a border. **Download** will output a JPEG version, up to 10000 pixels wide.

### Admin pages (web)

| Page | What it is for |
| --- | --- |
| **Sources & scan** | Add or remove folders, start or cancel a scan, generate missing thumbnails |
| **Settings** | Viewer password, transcode options, the `derived` folder |
| **Albums** | Regenerate folder albums, remove empty albums |
| **Faces** | Turn faces on or off, choose and download a model pack, search photos again |
| **Transcodes** | Video conversions in progress |
| **Memory** | How much memory the server uses. **Free memory now** lets go of what it can |

## Settings

Give settings on the command line (`--Gallery:DataDir=…`), in `appsettings.json` under `Gallery`, or as
environment variables (`Gallery__DataDir`). Run `Gallery.Web --help` to see every option and its
default. The most useful ones:

| Setting (`Gallery:…`) | Meaning |
| --- | --- |
| `DataDir` | Where the database, thumbnails and converted videos are kept |
| `Sources:0`, `Sources:1`, … | Folders to index. Used on first start; after that, manage them in **Admin → Sources & scan** |
| `InitialAdminPassword` | The `admin` password on first start |
| `FfmpegPath`, `FfprobePath` | Paths to ffmpeg and ffprobe, if not on the `PATH` |
| `HeifDecPath`, `HeifInfoPath` | Paths to libheif's tools |
| `DcrawEmuPath`, `RawIdentifyPath` | Paths to LibRaw's tools, for RAW editing |
| `Https:…` | Certificate settings. See [HTTPS](#https) |
| `Faces:Enabled` | Find faces when the models are present (default `true`) |
| `Faces:Pack` | `buffalo_l` (default) or `buffalo_s` |
| `Faces:ModelsDir` | Where the face models are kept (default `{DataDir}/models/faces`) |
| `Faces:Workers` | Threads used to find faces (default: half the processor cores) |
| `ProgressLogSeconds` | How often progress is logged (default 5; `0` turns it off) |

Changes made in **Admin → Faces** override `Faces:Enabled` and `Faces:Pack`.

Other start options:

- `--Urls="http://0.0.0.0:8080;https://0.0.0.0:8443"` changes the ports.
- `--verbose` logs every file that is indexed.

## Summary

- **Your files are safe.** Gallery only reads your folders and writes thumbnails, converted videos and
  edits to the data folder. If that folder sits inside a source folder, scans skip it.
- **Deleted and moved files.** While Gallery runs, deleted files leave the gallery within a few seconds.
  Files moved or renamed inside a source folder keep their tags and albums. Files deleted while Gallery
  was off are detected at restart.
- **Unplugged drives.** If a whole source folder is missing, it is assumed the drive is unplugged and
  leaves its items alone until the drive returns.
- **Duplicates.** Files with identical contents become one item wherever they are. Files with the same
  name and date (such as a JPEG and its RAW) also become one item.
- **RAW files** display using their embedded JPEG preview. LibRaw is needed only to edit them.
- **Back up** the data folder (mainly `gallery.db`) to keep your tags, albums, people and edits.
  Thumbnails and converted videos can be regenerated.

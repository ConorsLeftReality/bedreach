# BedReach

**Can't reach your computer from bed? Now you don't have to.**

BedReach turns your Android phone into a remote for your Windows, Linux or Mac computer:
play/pause, volume, seeking, a touchpad, a keyboard, and one-tap buttons for Netflix, NOW,
Disney+, Crunchyroll, YouTube, Spotify, Prime Video and more. If the streaming **app** is installed on
your computer, BedReach opens the app. If not, it opens the website.

## Downloads

| Device | Download |
| --- | --- |
| 📱 **Android phone** (the remote) | [BedReach.apk](https://github.com/ConorsLeftReality/bedreach/releases/latest/download/BedReach.apk) |
| 🪟 **Windows** 10 / 11 | [BedReach.exe](https://github.com/ConorsLeftReality/bedreach/releases/latest/download/BedReach.exe) |
| 🍎 **Mac** with Apple silicon (M1 or newer) | [bedreach-osx-arm64.tar.gz](https://github.com/ConorsLeftReality/bedreach/releases/latest/download/bedreach-osx-arm64.tar.gz) |
| 🍎 **Mac** with Intel | [bedreach-osx-x64.tar.gz](https://github.com/ConorsLeftReality/bedreach/releases/latest/download/bedreach-osx-x64.tar.gz) |
| 🐧 **Linux** (most PCs) | [bedreach-linux-x64.tar.gz](https://github.com/ConorsLeftReality/bedreach/releases/latest/download/bedreach-linux-x64.tar.gz) |
| 🐧 **Linux** on ARM (Raspberry Pi 4/5 and others) | [bedreach-linux-arm64.tar.gz](https://github.com/ConorsLeftReality/bedreach/releases/latest/download/bedreach-linux-arm64.tar.gz) |

All versions: [Releases](https://github.com/ConorsLeftReality/bedreach/releases).

You need **one phone app** plus **the computer app** for each computer you want to control.
The phone and computer must be on **the same Wi-Fi**.

---

## Setup guide

### Step 1: Install the computer app

<details open>
<summary><b>🪟 Windows</b></summary>

1. Download **BedReach.exe** and double-click it.
   - If Windows SmartScreen shows "Windows protected your PC", click **More info → Run anyway**.
     (The app isn't code-signed yet.)
2. A purple ▶ icon appears in the system tray (bottom-right, maybe under the **^** arrow) and a
   "Pair your phone" window opens.
3. When **Windows Firewall** asks, tick **Private networks** and click **Allow access**.
   - If your home Wi-Fi is set to *Public* in Windows, either change it to *Private*
     (Settings → Network & internet → Wi-Fi → your network → **Private network**) or tick both boxes.

BedReach now starts by itself every time Windows starts. You can change that from the tray icon menu.

Tip: move `BedReach.exe` somewhere permanent (e.g. `Documents`) before the first run, since
Windows remembers where it was when it set up start-at-login.

</details>

<details>
<summary><b>🍎 macOS</b></summary>

1. Download the right file for your Mac (Apple menu → About This Mac shows **Apple M…** or **Intel**).
2. Open **Terminal** and run:

   ```bash
   cd ~/Downloads
   tar -xzf bedreach-osx-*.tar.gz
   xattr -d com.apple.quarantine bedreach   # the app isn't notarised yet
   ./bedreach install
   ```

   This copies BedReach to `~/Library/Application Support/BedReach/`, starts it, and makes it start at login.
3. **Allow keyboard and mouse control:** System Settings → Privacy & Security → **Accessibility** →
   click **+**, press <kbd>⌘</kbd><kbd>⇧</kbd><kbd>G</kbd>, paste
   `~/Library/Application Support/BedReach/bedreach`, and turn it on.
4. If macOS asks whether BedReach may accept incoming network connections, click **Allow**.

</details>

<details>
<summary><b>🐧 Linux</b></summary>

1. Download the right file (`x64` for normal PCs, `arm64` for Raspberry Pi and other ARM boards).
2. Install the helper tools BedReach uses. On Debian/Ubuntu:

   ```bash
   sudo apt install xdotool playerctl zenity          # X11 desktops
   sudo apt install ydotool playerctl zenity          # Wayland (e.g. newer GNOME/KDE): ydotool instead of xdotool
   ```

   Fedora: `sudo dnf install xdotool playerctl zenity`. Arch: `sudo pacman -S xdotool playerctl zenity`.
   With ydotool, its service has to be running: `sudo systemctl enable --now ydotool`.
3. In a terminal:

   ```bash
   cd ~/Downloads
   tar -xzf bedreach-linux-*.tar.gz
   ./bedreach install
   ```

   This copies BedReach to `~/.local/bin/`, starts it, and makes it start when you log in.
4. If you use a firewall (e.g. `ufw`), allow BedReach on your home network:

   ```bash
   sudo ufw allow from 192.168.0.0/16 to any port 47800:47801 proto tcp
   sudo ufw allow from 192.168.0.0/16 to any port 47801 proto udp
   ```

Run `bedreach status` any time to see its address and paired phones.

</details>

### Step 2: Install the phone app (Android)

1. On your phone, open the [BedReach.apk](https://github.com/ConorsLeftReality/bedreach/releases/latest/download/BedReach.apk) link and download it.
2. Tap the downloaded file. Android will ask you to allow installing apps from your browser:
   tap **Settings → Allow from this source**, go back, and tap **Install**.
3. If Play Protect warns about an unknown app, tap **More details → Install anyway**.

> **iPhone?** There's no iPhone app yet. Apple only allows apps to be installed through the App Store,
> which needs a paid developer account.

### Step 3: Pair them (once)

1. Make sure the phone is on **the same Wi-Fi** as the computer.
2. Open **BedReach** on the phone. Your computer appears under **On this Wi-Fi**. Tap it.
3. The phone shows a 6-digit code, and the computer pops up the same code.
   **If they match, click Allow** on the computer.

That's it. From now on, opening the app connects straight away.

**Computer not showing up?** Tap **Enter IP** on the phone and type the address shown by clicking the
tray icon (Windows) or running `bedreach status` (Mac/Linux). If that also fails, it's almost always the firewall (see Step 1).

---

## Using BedReach

| Tab | What it does |
| --- | --- |
| **Remote** | Play/pause, next/previous, arrows (seek or volume in most players), OK, Esc, Fullscreen, Space, volume. **Your phone's volume buttons change the computer's volume.** |
| **Touchpad** | Drag to move the mouse, tap to click. Two fingers: drag to scroll, tap to right-click. |
| **Apps** | Open streaming services. Tiles marked **APP** open the installed app, and **WEB** opens the website. Also Switch app, Browser back, Show desktop, Close window. |
| ⌨ | Type into the computer (search boxes and so on). |
| ⏻ | Sleep, lock, or shut down in 60 seconds (you can cancel it). |

### Streaming apps

Out of the box BedReach knows Netflix, NOW, Disney+, Crunchyroll, YouTube, Spotify, Prime Video,
Apple TV, HBO Max, Twitch and Plex. For each one, the **computer** checks whether the app is installed
(Start menu on Windows, Applications folder on Mac, app launcher on Linux) and opens it. Otherwise it opens the website.
Install an app later and the tile switches to **APP** next time the phone connects.

To add or change buttons, edit the settings file:

| | Settings file |
| --- | --- |
| Windows | tray icon → **Edit app shortcuts** (`%APPDATA%\BedReach\config.json`) |
| macOS | `~/Library/Application Support/BedReach/config.json` |
| Linux | `~/.config/bedreach/config.json` |

```json
{ "id": "jellyfin", "name": "Jellyfin", "apps": ["Jellyfin Media Player"], "target": "http://my-server:8096", "color": "#00A4DC" }
```

`apps` lists installed-app names to look for, `target` is the website (or any link) to open
otherwise, and `color` is the tile colour. Restart BedReach afterwards (Windows: tray → Quit and open it
again; Mac/Linux: log out and in, or run `bedreach install` again).

---

## Troubleshooting

| Problem | Fix |
| --- | --- |
| Phone says **Can't reach PC** | Is the computer on and awake, with BedReach running? Same Wi-Fi? Check the firewall (Step 1). |
| Buttons do nothing on **Mac** | Give BedReach Accessibility permission (macOS Step 3), then run `bedreach install` again. |
| Buttons do nothing on **Linux** | Run `bedreach` in a terminal. It lists any missing helper tools. On Wayland you need `ydotool` and its service running. |
| Play/pause doesn't reach the video | Click the video once on the computer so its window is in front, or use **Space** on the Remote tab. |
| "Not paired any more" | The phone was removed on the computer. Pair it again. |
| Want to remove a phone | Windows: tray → **Paired phones…**. Mac/Linux: `bedreach status`, then `bedreach remove "Phone name"`. |

**Limits:** the computer has to be on and awake. The phone can put it to sleep but can't wake it.
Commands go to whichever window is in front, the same as a real keyboard.

## Privacy & security

- **Local only:** BedReach only works on your local network and never talks to the internet.
  (Don't port-forward 47800.)
- **Pairing:** each phone must be approved on the computer after you compare a 6-digit code.
  The code exchange (ECDH P-256 with a commitment) stops an attacker on the same Wi-Fi from sneaking in.
- **Encryption:** everything after pairing is encrypted and authenticated (AES-256-GCM, replay-protected).
- **Fixed commands:** the computer only accepts a fixed list of remote-control actions. There's
  no "run a command" or "open any website": it only opens the shortcuts in its own settings file.
- **Brute-force limit:** 5 failed attempts from one address blocks it for 5 minutes.
- **Stored keys:** they're protected by Windows DPAPI, the Android Keystore, and owner-only files on Mac/Linux.

Protocol details: [PROTOCOL.md](PROTOCOL.md).

---

## For developers

```
android/   Kotlin phone app (no third-party libraries)
core/      Shared C# server: discovery, pairing, encryption, settings
windows/   Windows tray app (WinForms)
agent/     Linux + macOS background agent
```

**Build:**

```bash
# Windows app (.NET 10 SDK)
dotnet publish windows/BedReach.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out

# Linux / macOS agent (replace the runtime: linux-x64, linux-arm64, osx-arm64, osx-x64)
dotnet publish agent/BedReach.Agent.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out

# Android app (JDK 17 + Android SDK 34)
cd android && ./gradlew assembleRelease
```

**Tests:** `cd android && ./gradlew testDebugUnitTest` runs the crypto tests. For the end-to-end test,
build the Windows app or agent in **Debug**, set `BEDREACH_TEST_DIR` to an empty folder, start it, and run
the same command with that variable set. Debug test mode listens on 127.0.0.1 only, auto-approves pairing,
and logs commands instead of pressing keys. That mode isn't compiled into Release builds.

**Releases:** pushing a tag like `v1.2.0` makes GitHub Actions build everything and attach it to a Release.
To sign the APK with your own key, add the repository secrets `BEDREACH_KEYSTORE_B64` (base64 of the
`.jks`), `BEDREACH_KEYSTORE_PASSWORD`, `BEDREACH_KEY_ALIAS` and `BEDREACH_KEY_PASSWORD`. Without them
the APK is signed with a throwaway debug key, so each release has to be uninstalled before installing the next.

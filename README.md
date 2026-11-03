# KeyShare

A small Windows 11 app that lets one keyboard and mouse control two PCs. Start
sharing on one PC, type its share code on the other, and move the mouse off the
edge of the screen to switch between them.

## Get the app

Every push builds the app on GitHub Actions. Open the latest
[**Build KeyShare** run](../../actions/workflows/build.yml), scroll to **Artifacts**, and download one:

- **KeyShare-small**: about 100 KB. Needs the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
  If it isn't installed, Windows offers to install it the first time you run the app.
- **KeyShare-standalone**: about 45 MB. Runs without installing anything.

Or build it yourself with the .NET 10 SDK:

```bash
dotnet publish KeyShare.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

Copy `KeyShare.exe` to both PCs. There is no installer. Settings are kept in
`%APPDATA%\KeyShare`.

## Use it

1. **On the PC with the keyboard and mouse:** choose *Share this PC's keyboard
   and mouse*, choose which side of your screen the other PC is on, and click
   **Start sharing**. A share code such as `K7QM-3XPD` appears. It stays the same
   until you click **New code**.
2. **On the other PC:** choose *Control this PC from the other one*, type the
   code, and click **Connect**. It finds the host on your network by itself.
   If it can't (some networks block broadcasts), type the host's IP address in
   **Host address**.
3. When Windows Firewall asks, allow KeyShare on **private networks**.

Then:

- Move the mouse off the edge you picked to control the other PC. Move it back
  off the opposite edge to return.
- **The switch key** (Ctrl+Alt+F12 unless you change it) switches PCs from
  anywhere. To pick your own, click **Change…** next to **Switch key** on the
  sharing PC and press a key or combination, or click a middle or side mouse
  button. Scroll Lock, Pause or a side mouse button work well in games. After
  switching with the key, a yellow ring briefly shows where the pointer is.
- **Full-screen games** lock the pointer in the middle of the screen, so it
  never reaches an edge. Use the switch key to leave and come back. A game on
  the PC being controlled gets real mouse movement, so you can play it with the
  shared mouse. If a game runs as administrator and the switch key does
  nothing, run KeyShare as administrator too.
- The edge doesn't switch while a mouse button is held, so dragging a window to
  snap it still works.
- The other PC's own keyboard and mouse keep working.
- **Clipboard:** copy on either PC and paste on the other. Text, formatted text
  (RTF, for Word and Outlook) and images are shared, up to 32 MB. Files are not.
  Turn it off with **Share clipboard**. Passwords copied from password managers
  that mark them as private (most do) are never sent.
- **Screen timeouts:** while you use either PC, the other one doesn't turn its
  screen off, go to sleep or lock. So you can work on one for an hour and the
  other is still unlocked when you switch to it. Walk away from both, and both
  time out and lock as they normally would. Turn this off with **Keep both PCs
  awake while I'm using either one**. (A work PC with a strict IT lock policy
  may still lock on its own schedule.)
- **Unlocking the other PC:** on the PC being controlled, tick **Let the other
  PC type on this PC's lock screen** and say yes to the admin prompt. You can
  then wake it and type your PIN or password with the shared keyboard. See
  [Lock-screen typing](#lock-screen-typing) below. After sleep or a lock,
  KeyShare reconnects within a few seconds.
- Tick **Start with Windows** on both PCs and they reconnect by themselves after
  a restart. If the link drops, the joining PC keeps retrying, and the host takes
  the mouse back right away so you are never stuck.
- Minimizing puts KeyShare in the system tray. Closing the window quits.

## How it works and what it doesn't do

- The host uses low-level keyboard and mouse hooks. While the other PC has
  control, local input is held back and sent over TCP port 47832. Discovery uses
  UDP port 47833. The joining PC replays the input with `SendInput`.
- Traffic is encrypted. Each connection makes a fresh key (ECDH P-256 plus the
  share code, AES-256-GCM), so someone listening on the network can't read your
  keystrokes, and a PC without the code can't connect. The host allows about one
  wrong code per second. The code has 40 bits, which is plenty for a home or
  office network, but don't expose the port to the internet.
- Both PCs need the same version of KeyShare. A mismatch shows an error instead
  of connecting.
- Keys are sent as physical scan codes, so each PC uses its own keyboard layout.
  If you switch PCs while holding a key, its release still goes to the PC that
  saw it pressed, so keys don't get stuck.
- Windows doesn't let normal apps control windows that run as administrator,
  such as Task Manager or UAC prompts. To control those, run KeyShare as
  administrator on the PC being controlled. **Ctrl+Alt+Del** can't be sent.
- One host and one joined PC. There's no file sharing.
- Problems are logged to `%APPDATA%\KeyShare\log.txt`.

## Lock-screen typing

Windows doesn't let normal apps type on the lock screen; only the built-in
SYSTEM account can. So this option installs a small Windows service named
**KeyShare unlock helper**:

- It copies `KeyShare.exe` to `C:\Program Files\KeyShare`, where only
  administrators can change it, and runs it as a service. It does nothing but
  keep a helper running in your session.
- While your PC is locked, KeyShare passes the keys it receives to that helper,
  which types them on the lock screen. The rest of the time the helper ignores
  input completely, so it can't be used to approve admin (UAC) prompts or
  control admin windows.
- Only your own Windows account can talk to the helper, and not over the
  network.
- It works when the PC is **locked** (KeyShare is still running). It doesn't
  work on the sign-in screen right after a restart or sign-out, because KeyShare
  isn't running yet. Sign in with that PC's own keyboard there.
- **After updating KeyShare,** untick and tick the box again so the service gets
  the new version too.
- Untick the box to remove the service and its files.

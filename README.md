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
- **KeyShare-standalone**: about 45 MB, that runs without installing anything.

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
- **Ctrl+Alt+F12** switches PCs from anywhere. It's also how you get back if the
  edges don't line up.
- The edge doesn't switch while a mouse button is held, so dragging a window to
  snap it still works.
- The other PC's own keyboard and mouse keep working.
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
- Keys are sent as physical scan codes, so each PC uses its own keyboard layout.
  If you switch PCs while holding a key, its release still goes to the PC that
  saw it pressed, so keys don't get stuck.
- Windows doesn't let normal apps control windows that run as administrator,
  such as Task Manager or UAC prompts. To control those, run KeyShare as
  administrator on the PC being controlled. **Ctrl+Alt+Del** can't be sent.
- One host and one joined PC. There's no clipboard or file sharing.
- Problems are logged to `%APPDATA%\KeyShare\log.txt`.

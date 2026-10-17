# Autopot

Community plugin for [GameHelper2](https://github.com/Gordin/GameHelper2) (Path of Exile 2).

Automatic flask triggers (HP/ES/mana logic), optional vitals overlay, and configurable safety TCP logout. Written by **MordWraith** for community forks. Build from source.

Also bundled in [MordWraith/Gamehelper-Core](https://github.com/MordWraith/Gamehelper-Core).

## Features

- Two independent hotkey triggers with cooldowns and threshold logic
- Multiple logic modes (life, ES, hybrid, mana combinations)
- Optional on-screen vitals bar (HP / ES / mana)
- Safety logout via TCP disconnect when vitals drop below configured thresholds
- DE/EN settings via GameHelper localization

## Requirements

- A working GameHelper2 source tree (Gordin core or MordWraith/Gamehelper-Core)
- **.NET 10 SDK** (`net10.0-windows`, x64)

## Build & install

```bash
git clone https://github.com/Gordin/GameHelper2.git
cd GameHelper2
git clone https://github.com/MordWraith/Autopot.git Plugins/Autopot
dotnet build Plugins/Autopot/Autopot.csproj -c Release
```

Enable **Autopot** in GameHelper → Plugins.

## Config

| Path | Purpose |
|------|---------|
| `config/settings.txt` | JSON settings (thresholds, keys, safety logout) |

## Credits

- **MordWraith** — plugin author and maintenance
- **Gordin / GameHelper2** — plugin host

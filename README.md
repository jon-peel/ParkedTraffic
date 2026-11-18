<img src="docs/icon.png" alt="ParkedTraffic icon" width="96" align="right">

# ParkedTraffic

Fills the gates and stands of the airport you're at in **Microsoft Flight Simulator 2020** with parked
[FSLTL](https://www.fslivetrafficliveries.com/) aircraft - handy when controlling on VATSIM and the
apron is empty. A terminal UI lists every stand so you can clear one for a VATSIM pilot, set
apron rules (domestic / international / cargo), and more.

## What it does

- **Finds your airport** from your aircraft's position (or pass an ICAO: `ParkedTraffic.exe FAOR`).
- **Reads the stands from the sim**: size, heading, jet bridges, cargo stands.
- **Picks realistic traffic**: live flights to/from the airport first (open ADS-B data from
  [adsb.lol](https://adsb.lol) / [adsb.fi](https://adsb.fi)), then airlines from the
  [VRS standing-data](https://github.com/vradarserver/standing-data) route table (CC0).
  Once-a-day airlines get at most one aircraft; models fit the stand; no turboprops at jet bridges.
- **Keeps out of the way**: an aircraft (VATSIM, FSLTL, yours) arriving on a stand removes ours.
- **Shows other aircraft** on stands with their callsign and lights (`BTSL`), and can switch a
  beacon on (`B`).

## Requirements

- Windows, MSFS 2020, and the FSLTL traffic base (`fsltl-traffic-base` in your Community folder).
- Nothing else: the release exe is self-contained.

## Use

Download `ParkedTraffic-win-x64.zip` from the Releases page, unzip anywhere, load into a flight,
and run `ParkedTraffic.exe`. Press `?` in the app for all keys. Settings (quota, apron and stand
rules, exclusions) are saved per airport in `%LOCALAPPDATA%\ParkedTraffic\config`, so they survive
moving or re-downloading the exe.

## Build

Needs the .NET 10 SDK.

```powershell
.\build.ps1        # -> dist\ParkedTraffic.exe (trimmed, single file, ~12 MB)
```

GitHub Actions builds the exe on every push to `main` (see the run's artifacts) and publishes a
release when a `v*` tag is pushed.

## Licence

MIT - see [LICENSE](LICENSE). `lib/SimConnect.dll` is part of the Microsoft Flight Simulator SDK
and is covered by Microsoft's terms.

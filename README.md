Reworks the ECG on the health chip so it looks closer to a real monitor. Align to most identifiable vanilla threshold, so vanilla experience remains valid.

## Changes
- Normal heartbeats: proper P/QRS/T timing, which QT will now properly scales with heart rate, and ST will sit on the baseline.
- Most vanilla behaviour like Heartbeat sound etc. still synced to the trace
- Hypothermia: J waves (or Osborn if you like) will appear below 33C body temp and longer Q-T period
- Hemothorax: smaller complexes (lower voltage/amplitude)

- Arrhythmia still follow the fibrillation value in stages:
### 15+
[cant send image]
occasional PVCs with a compensatory pause (Premature Ventricular Contraction, two heart beat spikes(R) will be close to each other)

### 30+
[pretend as img]
couplets and short runs of VT, means PVCs start coming in pairs or short bursts

### 50+
[img]
sustained VT, same threshold where the game shows Ventricular tachycardia
complexes fuse into a triangle wave as it gets worse, then round off into flutter around 75 (where worse debuff shows)

### 95-100
[img]
degrades into VF (The more realistic fibrillation behaviour)

### 100+ cardiac arrest
heart fails, VF that goes from coarse VF to fine VF as the brain fails, flatline on death

## Text changes for en and zhCN
- Fibrillation above 75 is now called Ventricular flutter instead of Fibrillation, which it wasn't in vanilla
- Cardiac arrest description now says ventricular fibrillation instead of asystole, when the heart still properly loses its functionalities
- Fix minor typo in zhCN

## Console
command `ecg` toggles between plus and vanilla monitor (`ecg plus` / `ecg vanilla`). Will not disable achievements upon using.

## Install
Install BepInEx 5 and drop EcgPlus.dll into BepInEx/plugins/

## Compatibility
works in existing saves, Should work alongside most other mods unless they also work on ECG.
should be client side only so it should be safe with multiplayer but I didn't test it.

## Building
Requires the .NET SDK. Point `GameDir` at your game folder:
```
dotnet build -c Release -p:GameDir="C:\path\to\Casualties Unknown"
```
The Release build copies `EcgPlus.dll` into `BepInEx/plugins/EcgPlus/` (fails while the game is running).

Reworks the ECG on the health chip so it looks closer to a real monitor. Align to most identifiable vanilla threshold, so vanilla experience remains valid.

Since 1.1.0 it also has two optional gameplay parts: a rework of defib shocks during cardiac arrest (on by default, can be turned off in the config) and a Long QT syndrome challenge (a run setting, off by default).

## Changes
- Normal heartbeats: proper P/QRS/T timing, which QT will now properly scales with heart rate, and ST will sit on the baseline.
- Most vanilla behaviour like Heartbeat sound etc. still synced to the trace
- Hypothermia: J waves (or Osborn if you like) will appear below 33C body temp and longer Q-T period
- Hemothorax: smaller complexes (lower voltage/amplitude)
- (1.1.0) Sinus beat closer to lead II: deeper Q, S about half of R, flat ST
- (1.1.0) PVCs look like real ones: wide and blunt, taller than sinus beats, with a deep inverted T (slow down, quick return). The blocked sinus P wave can show up as a small notch on the T
- (1.1.0) Artifacts: footsteps, landing from a jump, swinging/digging (muscle noise), getting hit, shivering when cold
- (1.1.0) Breathing slightly speeds up and slows down the heart rate at rest and in sleep (respiratory sinus arrhythmia)
- (1.1.0) Heartbeat sound follows pulse strength: an early beat is weak or silent, the beat after the pause is louder

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

## Big monitor (1.1.0)
- `ecgview` opens a large ECG monitor (Esc closes)
- `ecgview mark waves|types|all|off` labels P/Q/R/S/T/J waves and beat types (N, PVC, couplet, NSVT, VT). Add `force` to keep labels above 150 bpm
- The manual defibrillator screen uses the same monitor, with a MARK button to toggle the labels

## Defib shocks in cardiac arrest (1.1.0, config `ArrestShockRework`)
Vanilla: a shock in cardiac arrest restarts the heart 20% of the time.
Now it depends on how long the heart has been in VF:
- 90% while VF is still coarse, falling to 30% as it decays to fine VF
- only 20% of shocks are clean restarts (same as vanilla); the rest come back with fibrillation at 60-80 and can slide back into arrest if blood pressure or oxygen stay low

## Long QT syndrome challenge (1.1.0, run setting)
Found at the end of the challenges group. Fibrillation rises slowly whatever you do and never settles on its own:
- LQT1: exertion (running, low stamina, climbing, working out), doubled while swimming
- LQT3: sitting idle, sleeping, unconscious, slow heart rate
- LQT2: stress (adrenaline, horror, pain), plus a kick from loud noises and hard hits
- Cold below 36C speeds it all up
At a normal pace that is one treatment every 1.5 layers or so. You start with +2 INT, a manual defibrillator (small battery at 33%), a syringe with 20 ml of amiodarone and a small battery blueprint.
The QT interval on the monitor is longer, and the big monitor shows which subtype is driving it.
Command `lqt` shows the rates, `lqt <key> <value>` tunes them (saved to the config).

## Console
command `ecg` toggles between plus and vanilla monitor (`ecg plus` / `ecg vanilla`). Will not disable achievements upon using.

## Install
Install BepInEx 5 and drop EcgPlus.dll into BepInEx/plugins/

## Compatibility
works in existing saves, Should work alongside most other mods unless they also work on ECG.
the monitor part should be client side only so it should be safe with multiplayer but I didn't test it. The arrest shock rework and Long QT are gameplay changes and are untested in multiplayer.
A Long QT save still loads after removing the mod (the challenge just turns off): the setting is kept in `save.sv.ecgplus` next to the save instead of inside it.

## Building
Requires the .NET SDK. Point `GameDir` at your game folder:
```
dotnet build -c Release -p:GameDir="C:\path\to\Casualties Unknown"
```
The Release build copies `EcgPlus.dll` into `BepInEx/plugins/EcgPlus/` (fails while the game is running).

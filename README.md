# MindPuck.Unity

MindPuck in Unity. A puck sits on an air-hockey table and your eyes move it. Bring it to rest on
the goal pad to win the level. There are three control schemes, and **F1** steps through them:

- **Magnetic** (the default): the point you look at pulls the puck. Look where you want it to go.
- **Steer**: look at the edge of the puck on the side it should go, and it travels that way. Look at
  its centre to let it be. Your eyes stay on the puck the whole time.
- **Repulsor**: the point you look at **repels** the puck, like a black hole in reverse. Look where
  you do not want the puck to go, and look far away to let it glide.

Levels are generated from a seed and get harder as you go. Every level is played and won by a solver
bot, with an unsteady gaze, in all three modes, before you see it, so every level can be won each way.
The time the bot took in the current mode is shown as par.

This is the Unity version of [MindPuck.Web](../MindPuck.Web) (the web game). Gaze comes from
[MindAttic.EyeTracker](../MindAttic.EyeTracker): a browser page owns the webcam, a small bridge
relays the gaze over WebSocket, and this game receives it through the
`com.mindattic.eyetracker` Unity package. No camera code runs inside Unity.

## Run

Unity 6000.4.3f1. The project expects `MindAttic.EyeTracker` to be cloned next to it, because
`Packages/manifest.json` takes the gaze package by sibling path.

1. Start the bridge:
   ```
   cd D:\Projects\MindAttic\MindAttic.EyeTracker; npm run build; npm run bridge
   ```
2. Open the tracker page, `http://localhost:8787/tracker/`, and allow the camera.
3. Open this project in Unity, open `Assets/MindPuck/Scenes/MindPuck.unity`, and press Play.
4. Press **C** to calibrate: look at each of the nine dots until it fills, then follow the three
   amber head-movement steps (eyes on the dot, move your head).

You can skip steps 1, 2 and 4. With no tracker connected, the mouse pointer stands in for your gaze,
so the game is playable without a camera or a bridge.

## Controls

| Key | Does |
| --- | --- |
| F1 | Step to the next control scheme: magnetic, steer, repulsor. Restarts the attempt; the choice is remembered |
| R | Restart the level |
| N | Skip to the next level |
| B | Bot demo on or off: the solver bot plays the level live |
| C | Calibrate the eye tracker (Esc cancels) |
| M | Mouse stand-in on or off. On is the default, and it only takes over while no tracker is connected |

The HUD shows the level number, your time, par, and where the gaze is coming from. While it says
"waiting for tracker", open the tracker page. A label at the bottom centre always names the live
control scheme, for example "Mode 2 of 3  STEER  F1 next", with the name in the colour of the mode
(pink magnetic, green steer, blue repulsor). The gaze reticle uses the same colour.

At the start of every attempt the puck is held still for 0.9 seconds, so you can find it before
your gaze starts pushing it. The clock does not run during the hold.

## How it is built

```
Assets/MindPuck/
  Core/     MindPuck.Core    the rules. No engine references.
  Game/     MindPuck.Game    camera, table, meshes, loop, HUD. All built from code at runtime.
  Editor/   MindPuck.Editor  generates the scene, the template materials and the build settings.
  Tests/    MindPuck.Tests   NUnit EditMode tests for the core.
```

### The core mirrors the web core

`Assets/MindPuck/Core` is a port of `MindPuck.Web/src/core`, one file for one file:

| Web (TypeScript) | Unity (C#) | What it holds |
| --- | --- | --- |
| `rng.ts` | `Rng.cs` | mulberry32, mixSeed, range, gaussian |
| `geometry.ts` | `Geometry.cs` | `Vec`, polygon tests and distances, the obstacle shapes |
| `level.ts` | `Level.cs` | the table constants and the `Level` record |
| `physics.ts` | `Physics.cs` | `GazeMode`, `PhysicsParams`, `RepelAccel`, `PullAccel`, `SteerAccel`, `PuckSim` |
| `navgrid.ts` | `NavGrid.cs` | the free-cell grid and A* |
| `bot.ts` | `Bot.cs` | `SolverBot`, `Solver.SolveLevel` |
| `levelgen.ts` | `LevelGen.cs` | `GenerateLevel`, with the solver as the check |

It uses `double` everywhere, the same constants and the same algorithms. The random generator uses
`uint` arithmetic in `unchecked` blocks, so it produces the same sequence as the TypeScript one, and
the generator draws from it in the same order. A given seed and level number therefore describe the
same candidate level in both games. Two details differ on purpose:

- JavaScript's `Math.round` rounds halves up, so the port uses `Math.Floor(x + 0.5)`.
- `Math.hypot` becomes `Math.Sqrt(x * x + y * y)`.

Functions like `exp`, `sin` and `atan2` can differ in the last bit between JavaScript engines and
.NET. The simulation is sensitive to that over thousands of steps, so the bot's exact time (par), and
in rare cases which candidate passes, may differ from the web game. The rules do not.

The core has no engine references (`noEngineReferences`), so levels are generated on a worker
thread while the HUD says "Building level".

### The game layer

Nothing in the scene is authored by hand. The scene holds one object, `MindPuck`, and
`MindPuckGame` builds the rest when it wakes: the camera, the light, the table and rails, the walls
of the level, the goal pad, the puck and the gaze reticle. If you play a scene without that object,
`GameBootstrap` creates it.

- **Coordinates.** The core's table is 16 by 9 units, origin top-left, y down. In the scene it lies
  on the ground plane, centred on the origin: table x is world +X and table y is world -Z.
- **Gaze.** Normalized gaze (0..1, y down) covers the whole screen. A ray through that screen point
  meets the table plane at the point you are looking at. The camera looks down with a slight tilt and
  backs off until the whole table is on screen.
- **Meshes.** Walls are prisms extruded from the level polygons. Polygons can be concave (the L
  shape), so the caps are cut by ear clipping (`MeshBuilder.Triangulate`).
- **Materials.** Built-in render pipeline. Lit parts use `Standard`, markings use `Sprites/Default`.
  Each material is a runtime copy of a template in `Assets/MindPuck/Resources`, which is what keeps
  both shaders in a player build.
- **Loop.** `PuckSim.Advance(dt, gaze)` runs the fixed 120 Hz steps. The gaze is null, and pushes
  nothing, when its confidence is zero (no face, or a blink). In the bot demo the game steps the
  simulation itself, because the bot decides once per step.
- **Input.** Keys are read from IMGUI events, and the HUD is IMGUI, so no input package is needed.

### Regenerating the scene

The scene, the two template materials and the build settings come from
`MindPuck.Editor.ProjectSetup.CreateScene`, also on the menu as **MindPuck > Set Up Project**:

```
Unity.exe -batchmode -quit -projectPath D:\Projects\MindAttic\MindPuck.Unity -executeMethod MindPuck.Editor.ProjectSetup.CreateScene -logFile setup.log
```

### Building and checking a player

`MindPuck.Editor.ProjectSetup.BuildWindows` (menu **MindPuck > Build Windows Player**) builds
`Build/Windows/MindPuck.exe`. `Build/` is ignored by git.

```
Unity.exe -batchmode -quit -projectPath D:\Projects\MindAttic\MindPuck.Unity -executeMethod MindPuck.Editor.ProjectSetup.BuildWindows -logFile build.log
```

The player takes a few switches for checking it without a person at the keyboard:

| Switch | Does |
| --- | --- |
| `-mindpuck-bot` | Start with the bot demo on |
| `-mindpuck-mode attract\|steer\|repel` | Play that control scheme for this run, without changing the remembered choice |
| `-mindpuck-calibrate` | Start a calibration as soon as a tracker sends gaze |
| `-mindpuck-level <n>` | Start on level n, counted from one |
| `-mindpuck-shot <file.png>` | Save a screenshot, then quit |
| `-mindpuck-shot-after <seconds>` | When to take it (default 5) |
| `-mindpuck-quit-after <seconds>` | Quit after this long |

The game logs one line, starting `MindPuck:`, when the tracker count changes, when the first gaze
sample arrives, when a calibration ends, and on each win. For example:

```
Build\Windows\MindPuck.exe -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile run.log -mindpuck-bot -mindpuck-shot shot.png -mindpuck-shot-after 4
```

## Tests

`Assets/MindPuck/Tests` holds NUnit EditMode tests for the core: the random sequence against known
values from the TypeScript implementation, candidate levels against the TypeScript generator, wall
bounces, the route finder, and that generated levels are solvable. Run them from the Unity Test
Runner window.

## Limits

- The gaze client uses `System.Net.WebSockets.ClientWebSocket`, which does not work in WebGL builds.
- The calibration is for the window as it was when you calibrated. Calibrate again after resizing it.

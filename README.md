# SMPL Mecanim

A Unity (5.6.2f1) scene that renders the [SMPL](https://smpl.is.tue.mpg.de/) human body
model and drives it live over a WebSocket connection from
[MG-MotionLLM](https://arxiv.org/abs/2504.02478) (CVPR 2025) — a Python model that
generates SMPL motion and natural-language captions from text/motion prompts.

`MotionStreamClient` connects to the Python WebSocket server, applies each incoming
frame's pose to the SMPL avatar(s) in the scene, and shows the live caption on screen via
`CaptionDisplay`. When no stream is active, each avatar falls back to its own
Mecanim-driven idle loop.

## Setup

1. **Download the SMPL Unity package.** It's licensed for research use only, so it isn't
   committed to this repo (`assets/SMPL/` is gitignored, except `SMPLBlendshapes.cs`,
   which has a local modification). Download it from
   [smpl.is.tue.mpg.de/download.php](https://smpl.is.tue.mpg.de/download.php)
   (requires a free account) and extract its `Models/`, `Samples/`, and `Scripts/`
   contents into `assets/SMPL/`, so you end up with `assets/SMPL/Models`,
   `assets/SMPL/Samples`, `assets/SMPL/Scripts/mpi`, etc. alongside the already-tracked
   `SMPLBlendshapes.cs`.
2. **Download the idle/walk/run/crouch mocap clips.** These come from a paid Unity
   Asset Store package (`assets/raw mocap data/` is gitignored too) and back the
   `MyAnimation` Animator controller's idle-loop states. Get package #5330 from the
   [Unity Asset Store](https://www.assetstore.unity3d.com/en/#!/content/5330) and
   extract/import it into `assets/raw mocap data/`.
3. **Open the project** in Unity 5.6.x.
4. **Run a stream from MG-MotionLLM**, e.g.:
   ```
   python3 eval_m2t_stream.py --model_name ./m2t-ft-from-GSPretrained-base --name 000000
   ```
5. **Press Play.** `MotionStreamClient` auto-connects to `ws://127.0.0.1:8765` and starts
   driving the avatar as soon as the stream starts.

## Project layout

- `assets/Scripts/` — the WebSocket client (`MinimalWebSocketClient.cs`), the stream
  receiver that drives the avatars (`MotionStreamClient.cs`), and the caption overlay
  (`CaptionDisplay.cs`).
- `assets/SMPL/` — the SMPL model package (see Setup above).
- `assets/raw mocap data/` — idle/walk/run/crouch mocap clips from the Unity Asset Store
  (see Setup above).

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

1. **Download the SMPL Unity package.** It's licensed for research use only, so the model
   assets are *not* committed here — only the C# scripts under `assets/SMPL/Scripts/` (which
   carry local modifications) are tracked. Download the package from
   [smpl.is.tue.mpg.de/download.php](https://smpl.is.tue.mpg.de/download.php) (free account)
   and extract its `Models/`, `Samples/`, and `Scripts/` contents into `assets/SMPL/`, so
   you end up with `assets/SMPL/Models/`, `assets/SMPL/Samples/`, and the joint-regressor
   JSONs under `assets/SMPL/Scripts/mpi/jnt_regressors/`, alongside the already-tracked
   scripts.
2. **Assign the avatar materials.** The FBX imports without a material, so the body looks
   untextured until you apply one. For **each** avatar mesh — expand the avatar in the
   Hierarchy and select the mesh child (`f_avg` / `m_avg`) — in the Inspector set
   **Skinned Mesh Renderer → Materials → Element 0** to the matching sample material
   (`SMPL_f_sample` / `SMPL_m_sample`, from `assets/SMPL/Samples/Materials/`). See
   [`add_material.png`](add_material.png) for the exact field.
   *(The sample material's texture is the clothed-body image shipped with the SMPL package;
   swap in your own texture there for a different outfit.)*
3. **Download the idle/walk/run/crouch mocap clips.** These come from a paid Unity
   Asset Store package (`assets/raw mocap data/` is gitignored too) and back the
   `MyAnimation` Animator controller's idle-loop states. Get package #5330 from the
   [Unity Asset Store](https://www.assetstore.unity3d.com/en/#!/content/5330) and
   extract/import it into `assets/raw mocap data/`.
4. **Open the project** in Unity 5.6.x.
5. **Run a stream from MG-MotionLLM**, e.g.:
   ```
   python3 m2t_unity_stream.py --model_name ./m2t-ft-from-GSPretrained-base --name 000000
   ```
6. **Press Play.** `MotionStreamClient` auto-connects to `ws://127.0.0.1:8765` and starts
   driving the avatar as soon as the stream starts.

## Project layout

- `assets/Scripts/` — the WebSocket client (`MinimalWebSocketClient.cs`), the stream
  receiver that drives the avatars (`MotionStreamClient.cs`), and the caption overlay
  (`CaptionDisplay.cs`).
- `assets/SMPL/` — the SMPL model package. Only the C# scripts (`Scripts/`) are committed;
  the FBX models, materials/textures, betas, and joint-regressors are gitignored and must
  be downloaded + applied (see Setup above).
- `assets/raw mocap data/` — idle/walk/run/crouch mocap clips from the Unity Asset Store
  (see Setup above).

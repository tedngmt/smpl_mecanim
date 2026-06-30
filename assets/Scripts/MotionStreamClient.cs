/*
    Connects to the MG-MotionLLM Python WebSocket server (see
    C:\Linux\MG-MotionLLM\utils\unity_stream.py and *_unity_stream.py) and drives the
    SMPL avatar's bones frame-by-frame from the received motion + caption stream.

    Message schema sent by the server (one JSON object per text frame):
        {"type": "start", "name": str, "fps": float, "num_frames": int, ...caption fields}
        {"type": "frame", "frame": int, "joints": [[x,y,z] x 22], ...caption fields}
        {"type": "end", "name": str}
    "joints" are global joint positions (index 0 = pelvis); see SMPLModifyBones.updateBoneAnglesFromJoints.
    Caption fields vary by script: "caption" (m2t/m2dt) or "caption_m2t" + "caption_m2dt" (compare).

    No scene editing is required: this component bootstraps itself at runtime via
    RuntimeInitializeOnLoadMethod, so dropping these scripts into Assets/ is enough.
    To customize host/port, add this component to a GameObject in the editor instead --
    the bootstrap skips creating a second instance if one is already present.
*/
using System;
using System.Collections.Generic;
using UnityEngine;
using SimpleJSON;
using MinimalWebSocket;

public class MotionStreamClient : MonoBehaviour
{
    [Tooltip("Machine running m2t_unity_stream.py / m2dt_unity_stream.py / m2t_and_m2dt_unity_stream.py")]
    public string host = "127.0.0.1";
    public int port = 8765;
    public float reconnectInterval = 2.0f;
    [Tooltip("Smoothly blend between streamed frames at Unity's render rate (decouples " +
             "playback smoothness from the stream fps; makes slow-motion look smooth).")]
    public bool interpolate = true;
    [Tooltip("EXPERIMENTAL: drive bones from the streamed per-joint global rotations (captures " +
             "axial twist the position-based retarget cannot) instead of from joint positions. " +
             "Falls back to positions automatically if a frame carries no 'rotations' field.")]
    public bool driveByRotation = false;

    SMPLBlendshapes[] _avatars;
    BodyPartHighlighter[] _highlighters;
    CaptionDisplay _captionDisplay;
    WebSocketClient _socket;
    float _reconnectTimer;

    bool _isStreaming;
    Vector3[] _lastJoints;   // newest received target pose
    Vector3[] _prevJoints;   // previous target, to interpolate from
    Vector3[] _renderJoints; // reused buffer for the interpolated pose
    Quaternion[] _lastRot;   // newest received per-joint global rotations (Unity frame), or null
    Quaternion[] _prevRot;   // previous, to slerp from
    Quaternion[] _renderRot; // reused buffer for the interpolated rotations
    float _lastMsgTime;      // Time.time when _lastJoints arrived
    float _frameInterval = 0.05f;  // measured gap between frames (seconds)

    string _caption = "";
    string _captionM2t = "";
    string _captionM2dt = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindObjectOfType<MotionStreamClient>() != null)
            return;
        var go = new GameObject("MotionStreamClient (MG-MotionLLM)");
        go.AddComponent<MotionStreamClient>();
    }

    void Start()
    {
        _avatars = FindObjectsOfType<SMPLBlendshapes>();
        if (_avatars.Length == 0)
            Debug.LogWarning("[MotionStreamClient] No SMPLBlendshapes found in scene -- pose updates will be skipped.");

        // Ensure each avatar has a body-part highlighter (drives the emissive glow from the
        // stream's "highlight" field). Added at runtime so no scene editing is needed.
        _highlighters = new BodyPartHighlighter[_avatars.Length];
        for (int i = 0; i < _avatars.Length; i++)
        {
            var h = _avatars[i].GetComponent<BodyPartHighlighter>();
            if (h == null) h = _avatars[i].gameObject.AddComponent<BodyPartHighlighter>();
            _highlighters[i] = h;
        }

        _captionDisplay = FindObjectOfType<CaptionDisplay>();
        if (_captionDisplay == null)
            _captionDisplay = new GameObject("CaptionDisplay (MG-MotionLLM)").AddComponent<CaptionDisplay>();

        TryConnect();
    }

    void TryConnect()
    {
        var socket = new WebSocketClient();
        try
        {
            socket.Connect(host, port);
            _socket = socket;
            Debug.Log("[MotionStreamClient] Connected to ws://" + host + ":" + port);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[MotionStreamClient] Connect to ws://" + host + ":" + port + " failed (" + e.Message + "), will retry");
            _socket = null;
        }
    }

    void Update()
    {
        if (_socket == null || !_socket.IsConnected)
        {
            // A dropped/never-established connection is treated the same as a clean
            // "end" -- otherwise a killed server (crash, Ctrl+C) leaves the last pose
            // and caption stuck forever instead of releasing control back to idle.
            StopStreaming();

            _reconnectTimer += Time.deltaTime;
            if (_reconnectTimer >= reconnectInterval)
            {
                _reconnectTimer = 0f;
                TryConnect();
            }
            return;
        }

        string message;
        while (_socket.TryDequeue(out message))
            HandleMessage(message);
    }

    void LateUpdate()
    {
        // Applied after Unity's Animator update phase so a streamed pose always wins
        // over any Mecanim-driven animation on the same rig. Re-applied every frame
        // (not just the one frame a message arrived) since Python streams at the
        // clip's fps (~20-30) while Unity renders much faster -- holding the last pose
        // steady avoids flickering back to the Animator's idle pose in between messages.
        // Only released back to idle once streaming actually stops (see StopStreaming).
        if (_isStreaming && _lastJoints != null)
        {
            // Blend from the previous frame to the newest one over the measured frame
            // interval, so motion is smooth at the render rate regardless of stream fps.
            float t = 1f;
            if (interpolate && _frameInterval > 0f)
                t = Mathf.Clamp01((Time.time - _lastMsgTime) / _frameInterval);

            Vector3[] pose = _lastJoints;
            if (interpolate && _prevJoints != null && _prevJoints.Length == _lastJoints.Length)
            {
                if (_renderJoints == null || _renderJoints.Length != _lastJoints.Length)
                    _renderJoints = new Vector3[_lastJoints.Length];
                for (int j = 0; j < _lastJoints.Length; j++)
                    _renderJoints[j] = Vector3.Lerp(_prevJoints[j], _lastJoints[j], t);
                pose = _renderJoints;
            }

            if (driveByRotation && _lastRot != null)
            {
                // Rotation-driven path: pose bones from streamed global orientations (twist-aware).
                Quaternion[] rotPose = _lastRot;
                if (interpolate && _prevRot != null && _prevRot.Length == _lastRot.Length)
                {
                    if (_renderRot == null || _renderRot.Length != _lastRot.Length)
                        _renderRot = new Quaternion[_lastRot.Length];
                    for (int j = 0; j < _lastRot.Length; j++)
                        _renderRot[j] = Quaternion.Slerp(_prevRot[j], _lastRot[j], t);
                    rotPose = _renderRot;
                }
                for (int i = 0; i < _avatars.Length; i++)
                    _avatars[i].ApplyStreamedGlobalRotations(pose, rotPose);
            }
            else
            {
                for (int i = 0; i < _avatars.Length; i++)
                    _avatars[i].ApplyStreamedJoints(pose);
            }
        }
    }

    void HandleMessage(string json)
    {
        JSONNode node;
        try { node = JSON.Parse(json); }
        catch (Exception e)
        {
            Debug.LogWarning("[MotionStreamClient] Bad JSON message: " + e.Message);
            return;
        }

        string type = node["type"];
        switch (type)
        {
            case "start":
                _isStreaming = true;
                _prevJoints = null;
                _lastJoints = null;
                _prevRot = null;
                _lastRot = null;
                _lastMsgTime = 0f;
                float startFps = node["fps"].AsFloat;
                if (startFps > 0.01f) _frameInterval = 1f / startFps;  // initial estimate; refined per frame
                UpdateCaptions(node);
                Debug.Log("[MotionStreamClient] start '" + node["name"].Value + "' (" +
                          node["num_frames"].AsInt + " frames @ " + node["fps"].AsFloat + " fps)");
                break;

            case "frame":
                UpdateCaptions(node);
                UpdateHighlight(node);
                ApplyFrame(node);
                break;

            case "end":
                Debug.Log("[MotionStreamClient] end '" + node["name"].Value + "'");
                StopStreaming();
                break;
        }
    }

    void StopStreaming()
    {
        if (!_isStreaming) return;
        _isStreaming = false;
        _caption = _captionM2t = _captionM2dt = "";
        if (_captionDisplay != null) _captionDisplay.Clear();
        if (_highlighters != null)
            foreach (var h in _highlighters)
                if (h != null) h.ClearHighlight();
    }

    // Read the per-frame "highlight" list (joint indices the active caption refers to) and
    // forward it to each avatar's highlighter. Absent/empty -> nothing highlighted.
    readonly List<int> _highlight = new List<int>();
    void UpdateHighlight(JSONNode node)
    {
        if (_highlighters == null) return;
        _highlight.Clear();
        JSONNode hl = node["highlight"];
        if (hl != null)
            for (int i = 0; i < hl.Count; i++)
                _highlight.Add(hl[i].AsInt);
        foreach (var h in _highlighters)
            if (h != null) h.SetActiveJoints(_highlight);
    }

    void UpdateCaptions(JSONNode node)
    {
        if (node["caption"] != null) _caption = node["caption"].Value;
        if (node["caption_m2t"] != null) _captionM2t = node["caption_m2t"].Value;
        if (node["caption_m2dt"] != null) _captionM2dt = node["caption_m2dt"].Value;

        if (_captionDisplay == null) return;

        if (!string.IsNullOrEmpty(_captionM2t) || !string.IsNullOrEmpty(_captionM2dt))
            _captionDisplay.SetCaptions(_captionM2t, _captionM2dt);
        else
            _captionDisplay.SetCaption(_caption);
    }

    void ApplyFrame(JSONNode node)
    {
        JSONNode jointsNode = node["joints"];
        if (jointsNode == null) return;

        int numJoints = jointsNode.Count;
        var joints = new Vector3[numJoints];
        for (int i = 0; i < numJoints; i++)
        {
            JSONNode p = jointsNode[i];
            joints[i] = new Vector3(p[0].AsFloat, p[1].AsFloat, p[2].AsFloat);
        }

        // Optional per-joint global rotations (Unity-frame quaternions, x,y,z,w). Present only
        // when the sender includes them; null otherwise so driveByRotation falls back cleanly.
        JSONNode rotNode = node["rotations"];
        Quaternion[] rots = null;
        if (rotNode != null && rotNode.Count > 0)
        {
            rots = new Quaternion[rotNode.Count];
            for (int i = 0; i < rotNode.Count; i++)
            {
                JSONNode q = rotNode[i];
                rots[i] = new Quaternion(q[0].AsFloat, q[1].AsFloat, q[2].AsFloat, q[3].AsFloat);
            }
        }

        // Keep the previous target and measure the arrival gap so LateUpdate can
        // interpolate between frames (auto-adapts to --fps and --speed on the sender).
        _prevJoints = _lastJoints;
        _prevRot = _lastRot;
        if (_lastMsgTime > 0f)
            _frameInterval = Mathf.Clamp(Time.time - _lastMsgTime, 0.001f, 0.5f);
        _lastMsgTime = Time.time;
        _lastJoints = joints;
        _lastRot = rots;
    }

    void OnApplicationQuit()
    {
        if (_socket != null) _socket.Disconnect();
    }
}

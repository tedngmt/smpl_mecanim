/*
    Highlights body parts on the single SMPL SkinnedMeshRenderer by adding an emissive glow
    to the mesh region that belongs to the joints named in each frame's "highlight" list
    (sent by the MG-MotionLLM Python stream). No second mesh, no skeleton overlay: it puts
    the MGMotionLLM/BodyPartHighlight material (same albedo, plus an emissive input) on the
    body and bakes a per-vertex glow mask each frame.

    Editing the look in Unity:
      - Create a Material from the "MGMotionLLM/BodyPartHighlight" shader
        (right-click in Project > Create > Material, set its Shader), then drag it into the
        "Highlight Material" slot on this component. Its Highlight Color, Highlight Strength,
        Smoothness, etc. are then editable in the normal material inspector, live in Play mode.
      - If you leave the slot empty, a material is created from the shader's defaults at
        runtime (works out of the box, but not editable as an asset).

    How the mask is built:
      - Each vertex is skinned to up to 4 bones with weights. We map every bone to a joint
        index (0..21, with the hand bones folded onto the wrists) once at startup.
      - glow(vertex) = sum over its bones of weight * intensity[joint], so the glow feathers
        smoothly across shoulders/elbows/etc. instead of cutting hard at joint boundaries.
      - intensity[joint] eases toward 1 for highlighted joints and 0 otherwise, so parts
        fade in/out over ~0.15s instead of popping as captions change every 0.5s.

    Driven by MotionStreamClient: SetActiveJoints(indices) each frame, ClearHighlight() on
    stop. Requires the body mesh to be Read/Write enabled (the SMPL fbx already is).
*/
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(SkinnedMeshRenderer))]
public class BodyPartHighlighter : MonoBehaviour
{
    [Tooltip("Material using the MGMotionLLM/BodyPartHighlight shader. Edit its Highlight " +
             "Color / Strength / Smoothness in the inspector. Leave empty to build one from " +
             "the shader defaults at runtime.")]
    public Material highlightMaterial;

    [Tooltip("Seconds for a part to fade fully in or out.")]
    public float fadeSeconds = 0.15f;

    // Joint-name -> index, matching the Python stream order (utils.unity_stream.UNITY_JOINT_NAMES
    // / SMPLModifyBones._boneNameToJointIndex). Hands are folded onto the wrists so the hand
    // mesh glows together with the arm.
    static readonly Dictionary<string, int> NameToJoint = new Dictionary<string, int>
    {
        {"Pelvis",0},{"L_Hip",1},{"R_Hip",2},{"Spine1",3},{"L_Knee",4},{"R_Knee",5},
        {"Spine2",6},{"L_Ankle",7},{"R_Ankle",8},{"Spine3",9},{"L_Foot",10},{"R_Foot",11},
        {"Neck",12},{"L_Collar",13},{"R_Collar",14},{"Head",15},{"L_Shoulder",16},
        {"R_Shoulder",17},{"L_Elbow",18},{"R_Elbow",19},{"L_Wrist",20},{"R_Wrist",21},
        {"L_Hand",20},{"R_Hand",21},
    };
    const int NumJoints = 22;

    SkinnedMeshRenderer _smr;
    Mesh _meshClone;
    Color[] _colors;            // reused vertex-colour buffer (alpha = glow)
    BoneWeight[] _weights;      // per-vertex skin weights
    int[] _boneToJoint;         // bone index -> joint index (-1 if unmapped)

    readonly float[] _intensity = new float[NumJoints];        // current, eased
    readonly float[] _targetIntensity = new float[NumJoints];  // requested
    bool _built;

    /// Set the joints to highlight this frame (indices 0..21, as sent in the stream's "highlight").
    public void SetActiveJoints(IList<int> jointIndices)
    {
        for (int j = 0; j < NumJoints; j++) _targetIntensity[j] = 0f;
        if (jointIndices != null)
            foreach (int j in jointIndices)
                if (j >= 0 && j < NumJoints) _targetIntensity[j] = 1f;
    }

    public void ClearHighlight()
    {
        for (int j = 0; j < NumJoints; j++) _targetIntensity[j] = 0f;
    }

    void EnsureBuilt()
    {
        if (_built) return;
        _smr = GetComponent<SkinnedMeshRenderer>();
        if (_smr == null || _smr.sharedMesh == null) return;

        // Clone the mesh so writing vertex colours never touches the shared SMPL asset. Done
        // lazily (first highlight) so it runs after SMPLBlendshapes / SMPLModifyBones setup.
        if (!_smr.sharedMesh.isReadable)
        {
            Debug.LogWarning("[BodyPartHighlighter] Mesh is not Read/Write enabled; highlighting disabled.");
            _built = true;   // do not retry every frame
            return;
        }
        _meshClone = Instantiate(_smr.sharedMesh);
        _smr.sharedMesh = _meshClone;

        _weights = _meshClone.boneWeights;
        int n = _meshClone.vertexCount;
        _colors = new Color[n];
        for (int i = 0; i < n; i++) _colors[i] = new Color(0f, 0f, 0f, 0f);
        _meshClone.colors = _colors;

        // Map each bone to a joint index by matching the joint name as a suffix of the bone
        // name (SMPL bones carry a prefix like "m_avg_" / "f_avg_").
        Transform[] bones = _smr.bones;
        _boneToJoint = new int[bones.Length];
        for (int b = 0; b < bones.Length; b++)
        {
            _boneToJoint[b] = -1;
            string bn = bones[b] != null ? bones[b].name : "";
            foreach (var kv in NameToJoint)
                if (bn.EndsWith(kv.Key)) { _boneToJoint[b] = kv.Value; break; }
        }

        // Pick the highlight material: the assigned asset if any (edit it in the material
        // inspector), else one from the shader defaults. The body's current albedo/tint is
        // copied in only when the chosen material doesn't already set a texture, so the skin
        // is preserved out of the box but never overrides one you set yourself.
        Material src = highlightMaterial;
        if (src == null)
        {
            Shader shader = Shader.Find("MGMotionLLM/BodyPartHighlight");
            if (shader == null)
            {
                Debug.LogWarning("[BodyPartHighlighter] Shader 'MGMotionLLM/BodyPartHighlight' not found and no Highlight Material assigned.");
                _built = true;
                return;
            }
            src = new Material(shader);
        }

        Material baseMat = _smr.sharedMaterial;
        bool hasTex = src.HasProperty("_MainTex") && src.GetTexture("_MainTex") != null;
        if (baseMat != null && !hasTex)
        {
            if (src.HasProperty("_MainTex") && baseMat.HasProperty("_MainTex"))
                src.SetTexture("_MainTex", baseMat.GetTexture("_MainTex"));
            else
                src.mainTexture = baseMat.mainTexture;
            if (src.HasProperty("_Color") && baseMat.HasProperty("_Color"))
                src.SetColor("_Color", baseMat.GetColor("_Color"));
        }

        // Use the material directly (not an instance) so live inspector edits show immediately.
        var mats = _smr.sharedMaterials;
        for (int i = 0; i < mats.Length; i++) mats[i] = src;
        _smr.sharedMaterials = mats;

        _built = true;
    }

    void LateUpdate()
    {
        EnsureBuilt();
        if (_meshClone == null || _colors == null) return;

        // Ease current intensities toward their targets.
        float step = (fadeSeconds <= 0f) ? 1f : Time.deltaTime / fadeSeconds;
        bool changed = false;
        for (int j = 0; j < NumJoints; j++)
        {
            float prev = _intensity[j];
            _intensity[j] = Mathf.MoveTowards(prev, _targetIntensity[j], step);
            if (!Mathf.Approximately(prev, _intensity[j])) changed = true;
        }
        if (!changed) return;

        // Rebake per-vertex glow = weighted sum of its bones' joint intensities (0..1). All
        // appearance (colour, strength) lives in the material; this only sets the mask.
        for (int v = 0; v < _colors.Length; v++)
        {
            BoneWeight w = _weights[v];
            float g = 0f;
            g += WeightContribution(w.boneIndex0, w.weight0);
            g += WeightContribution(w.boneIndex1, w.weight1);
            g += WeightContribution(w.boneIndex2, w.weight2);
            g += WeightContribution(w.boneIndex3, w.weight3);
            _colors[v].a = Mathf.Clamp01(g);
        }
        _meshClone.colors = _colors;
    }

    float WeightContribution(int boneIndex, float weight)
    {
        if (weight <= 0f || boneIndex < 0 || boneIndex >= _boneToJoint.Length) return 0f;
        int joint = _boneToJoint[boneIndex];
        return (joint < 0) ? 0f : weight * _intensity[joint];
    }
}

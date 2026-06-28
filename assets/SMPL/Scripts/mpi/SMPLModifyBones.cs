/*
License:
--------
Copyright 2017 Naureen Mahmood and the Max Planck Gesellschaft.  
All rights reserved. This software is provided for research purposes only.
By using this software you agree to the terms of the SMPL Model license here http://smpl.is.tue.mpg.de/license

To get more information about SMPL and other downloads visit: http://smpl.is.tue.mpg.
For comments or questions, please email us at: smpl@tuebingen.mpg.de

Special thanks to Joachim Tesch and Max Planck Institute for Biological Cybernetics 
in helping to create and test these scripts for Unity.

This is a demo version of the scripts & sample project for using the SMPL model's shape-blendshapes 
& corrective pose-blendshapes inside Unity. We would be happy to receive comments, help and suggestions 
on improving the model and in making it available on more platforms. 


	About this Script:
	==================
	This script file defines the SMPLModifyBones class which updates the joints of the model after a new 
	shape has been defined using the 'shapeParmsJSON' field and SMPLJointCalculator class has computed 
	the new joints. 
*/
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class SMPLModifyBones {

	private SkinnedMeshRenderer targetRenderer;

	private Transform[] _bones = null;
	private Transform[] _bonesBackup = null;

    private string _boneNamePrefix;
    private Dictionary<string, int> _boneNameToJointIndex;

    private bool _initialized;
    private bool _bonesAreModified;

    private Transform _pelvis;
    private Vector3[] _bonePositions;
    private Mesh _bakedMesh = null;

    // --- Position-based retarget state (see updateBoneAnglesFromJoints) ---
    private bool _retargetCaptured = false;
    private Transform[] _jointBones;       // live bone per joint index 0..21
    private Quaternion[] _bindRotLocal;    // bind orientation in avatar-local space
    private Vector3[] _bindDirLocal;       // bind bone direction (to primary child), avatar-local
    private Quaternion _pelvisBindBasis;   // bind hip-line/spine basis, avatar-local
    // Primary child joint index per joint (-1 = leaf / no aim). Indices match
    // _boneNameToJointIndex (Pelvis=0..R_Wrist=21). Pelvis (0) is oriented from a full
    // basis instead of a single child, so it is -1 here.
    private static readonly int[] _primaryChild = new int[] {
        -1, 4, 5, 6, 7, 8, 9, 10, 11, 12, -1, -1, 15, 16, 17, -1, 18, 19, 20, 21, -1, -1 };
    // Parent joint index per joint (-1 = root). Used so a child can inherit its parent's
    // twist instead of being given an independent world rotation.
    private static readonly int[] _parent = new int[] {
        -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19 };

    // Spine chain + neck get a FULL orientation basis (not just aim-at-child) so body
    // *twist* (turning about the vertical axis) is captured -- aim-only bones can't, which
    // shears the torso mesh when the person turns. The left/right reference blends from the
    // hip line (bottom) to the shoulder line (top) along the spine.
    private static readonly int[] _torsoJoints = { 3, 6, 9, 12 };   // Spine1, Spine2, Spine3, Neck
    private static readonly float[] _torsoBlend = { 0.34f, 0.67f, 1.0f, 1.0f };  // hips->shoulders
    private bool[] _isTorso;                // per joint, true for the spine/neck chain
    private float[] _torsoBlendByJoint;     // per joint hips->shoulders blend factor
    private Quaternion[] _torsoBindBasis;   // bind basis for each torso joint, avatar-local

    // Hip (thigh) twist: swing-only leaves the thigh roll arbitrary, so the knee can point
    // the wrong way. Recover it from the knee->ankle direction as a bend pole. The bind pose
    // has straight legs (no bend plane), so the bind twist is anchored to the body's
    // backward direction (knees bend back). If the thighs come out twisted ~180 deg, flip
    // HIP_POLE_SIGN to +1f.
    private const float HIP_POLE_SIGN = -1f;
    private bool[] _isHipBasis;             // true for L/R Hip when a bend pole was captured
    private Quaternion[] _hipBindBasis;     // bind basis for L/R Hip (indices 1, 2), avatar-local

	public SMPLModifyBones(SkinnedMeshRenderer tr)
    {
		targetRenderer = tr;

        _initialized = false;
        _bonesAreModified = false;

        _boneNamePrefix = "";

        _boneNameToJointIndex = new Dictionary<string, int>();

        _boneNameToJointIndex.Add("Pelvis", 0);
        _boneNameToJointIndex.Add("L_Hip", 1);
        _boneNameToJointIndex.Add("R_Hip", 2);
        _boneNameToJointIndex.Add("Spine1", 3);
        _boneNameToJointIndex.Add("L_Knee", 4);
        _boneNameToJointIndex.Add("R_Knee", 5);
        _boneNameToJointIndex.Add("Spine2", 6);
        _boneNameToJointIndex.Add("L_Ankle", 7);
        _boneNameToJointIndex.Add("R_Ankle", 8);
        _boneNameToJointIndex.Add("Spine3", 9);
        _boneNameToJointIndex.Add("L_Foot", 10);
        _boneNameToJointIndex.Add("R_Foot", 11);
        _boneNameToJointIndex.Add("Neck", 12);
        _boneNameToJointIndex.Add("L_Collar", 13);
        _boneNameToJointIndex.Add("R_Collar", 14);
        _boneNameToJointIndex.Add("Head", 15);
        _boneNameToJointIndex.Add("L_Shoulder", 16);
        _boneNameToJointIndex.Add("R_Shoulder", 17);
        _boneNameToJointIndex.Add("L_Elbow", 18);
        _boneNameToJointIndex.Add("R_Elbow", 19);
        _boneNameToJointIndex.Add("L_Wrist", 20);
        _boneNameToJointIndex.Add("R_Wrist", 21);
        _boneNameToJointIndex.Add("L_Hand", 22);
        _boneNameToJointIndex.Add("R_Hand", 23);

        _bakedMesh = new Mesh();
    }

	// Use this for initialization
	public bool initialize() {
		if (targetRenderer == null)
		{
			Debug.LogError("ERROR: The script should be added to the SkinnedMeshRenderer object");
			return false;
		}

		_bones = targetRenderer.bones;
        _bonePositions = new Vector3[_bones.Length];
        _bonesBackup = new Transform[_bones.Length];
        _cloneBones(_bones, _bonesBackup);

        // Determine bone name prefix
        foreach (Transform bone in _bones)
        {
            if (bone.name.EndsWith("root"))
            {
                int index = bone.name.IndexOf("root");
                _boneNamePrefix = bone.name.Substring(0, index);
                break;
            }
        }

        // Determine pelvis node
        foreach (Transform bone in _bones)
        {
            if (bone.name.EndsWith("Pelvis"))
            {
                _pelvis = bone;
                break;
            }
        }

        Debug.Log("INFO: Bone name prefix: '" + _boneNamePrefix + "'");
        _initialized = true;
		return true;
    }

	public Transform[] getBones()
	{
		return _bones;
	}
		
	public Dictionary<string, int> getB2J_indices()
	{
		return _boneNameToJointIndex;
	}

    public Transform getPelvis()
    {
        return _pelvis;
    }

    public Vector3[] getBonePositions()
    {
        return _bonePositions;
    }

	public string getBoneNamePrefix()
	{
		return _boneNamePrefix;
	}

    public bool updateBonePositions(Vector3[] newPositions, bool feetOnGround = true)
    {
        if (! _initialized)
            return false;

        float heightOffset = 0.0f;

        int pelvisIndex = -1;
		for (int i=0; i<_bones.Length; i++)
		{
            int index;
            string boneName = _bones[i].name;

            // Remove f_avg/m_avg prefix
            boneName = boneName.Replace(_boneNamePrefix, "");

            if (boneName == "root")
                continue;

            if (boneName == "Pelvis")
                pelvisIndex = i;


            Transform avatarTransform = targetRenderer.transform.parent;
            if (_boneNameToJointIndex.TryGetValue(boneName, out index))
            {
                // Incoming new positions from joint calculation are centered at origin in world space
                // Transform to avatar position+orientation for correct world space position
                _bones[i].position = avatarTransform.TransformPoint(newPositions[index]);
                _bonePositions[i] = _bones[i].position;
            }
            else
            {
                Debug.LogError("ERROR: No joint index for given bone name: " + boneName);
            }
		}

        _setBindPose(_bones);

        if (feetOnGround)
        {
            Vector3 min = new Vector3();
            Vector3 max = new Vector3();
            _localBounds(ref min, ref max);
            heightOffset = -min.y;

            _bones[pelvisIndex].Translate(0.0f, heightOffset, 0.0f);

            // Update bone positions to reflect new pelvis position
            for (int i=0; i<_bones.Length; i++)
            {
                _bonePositions[i] = _bones[i].position;
            }
        }

        return true;
		
	}

	public bool updateBoneAngles(float[][] pose, float[] trans)
	{	
		Quaternion quat;
		int pelvisIndex = -1;

		for (int i=0; i<_bones.Length; i++)
		{
			int index;
			string boneName = _bones[i].name;

			// Remove f_avg/m_avg prefix
			boneName = boneName.Replace(_boneNamePrefix, "");

			if (boneName == "root") {
				continue;
			}

			if (boneName == "Pelvis")
				pelvisIndex = i;
			
			if (_boneNameToJointIndex.TryGetValue(boneName, out index))
			{
				quat.x = pose [index][0];
				quat.y = pose [index][1];
				quat.z = pose [index][2];
				quat.w = pose [index][3];

				/*	Quaternions */
				_bones[i].localRotation = quat;
			}
			else
			{
				Debug.LogError("ERROR: No joint index for given bone name: " + boneName);
			}
		}
			
		_bones[pelvisIndex].localPosition = new Vector3(trans[0], trans[1], trans[2]);
		_bonesAreModified = true;
		return true;
	}


	/*  Position-based retarget: drive the rig from global joint positions instead of
	 *  per-joint rotations. HumanML3D/T2M local rotations use a different forward-kinematics
	 *  convention than Unity's localRotation, so they cannot be applied directly; joint
	 *  positions are convention-free. Each bone is rotated so it aims from its joint toward
	 *  its child joint (swing only -- twist about the bone axis is left at the bind value,
	 *  which positions don't constrain). The pelvis is oriented from a full basis built
	 *  from the hip line and spine direction.
	 *
	 *  `targets` are joint positions in the avatar's local (motion) space, indexed
	 *  Pelvis=0 .. R_Wrist=21. Bones are set in ascending joint order, which is
	 *  parent-before-child for the SMPL hierarchy, so setting world rotations is safe.
	 */
	public bool updateBoneAnglesFromJoints(Vector3[] targets)
	{
		if (!_initialized || targets == null || targets.Length < 22)
			return false;

		Transform avatarT = targetRenderer.transform.parent;
		if (!_retargetCaptured)
			_captureRetargetBind(avatarT);

		Quaternion aRot = avatarT.rotation;

		// Pelvis: full orientation from spine direction (up) and hip line (right).
		Vector3 upT = (targets[3] - targets[0]).normalized;        // Pelvis -> Spine1
		Vector3 rightT = (targets[2] - targets[1]).normalized;     // L_Hip -> R_Hip
		Quaternion basisT = Quaternion.LookRotation(Vector3.Cross(rightT, upT).normalized, upT);
		Quaternion pelvisLocal = (basisT * Quaternion.Inverse(_pelvisBindBasis)) * _bindRotLocal[0];
		_jointBones[0].rotation = aRot * pelvisLocal;
		_jointBones[0].position = avatarT.TransformPoint(targets[0]);

		for (int i = 1; i < 22; i++)
		{
			int c = _primaryChild[i];
			if (c < 0 || _jointBones[i] == null)
				continue;
			Vector3 dT = targets[c] - targets[i];
			if (dT.sqrMagnitude < 1e-12f)
				continue;

			if (_isTorso[i])
			{
				// Spine/neck: full basis (up = toward child, right = blended hip/shoulder
				// line) so torso twist is reproduced instead of shearing the mesh.
				Vector3 up = dT.normalized;
				Vector3 right = TorsoRight(targets, _torsoBlendByJoint[i]);
				Quaternion bT = Quaternion.LookRotation(Vector3.Cross(right, up).normalized, up);
				_jointBones[i].rotation = aRot * ((bT * Quaternion.Inverse(_torsoBindBasis[i])) * _bindRotLocal[i]);
			}
			else
			{
				bool done = false;

				// Hip (thigh): recover twist from the knee->ankle bend pole so the knee
				// points the right way. Other limbs stay swing-only.
				if ((i == 1 || i == 2) && _isHipBasis[i])
				{
					int g = _primaryChild[c];   // ankle
					Quaternion tgtBasis;
					if (g >= 0 && TryAimPoleBasis(dT, targets[g] - targets[c], out tgtBasis))
					{
						_jointBones[i].rotation = aRot * ((tgtBasis * Quaternion.Inverse(_hipBindBasis[i])) * _bindRotLocal[i]);
						done = true;
					}
				}

				if (!done)
				{
					// Parent-relative swing: inherit the parent's (already-set) world rotation
					// so the child follows the parent's twist, then bend only to aim at the
					// next joint. Avoids the child being twisted relative to its parent.
					int p = _parent[i];
					Quaternion inheritedWorld = _jointBones[p].rotation
						* (Quaternion.Inverse(_bindRotLocal[p]) * _bindRotLocal[i]);
					Vector3 inheritedAim = inheritedWorld
						* (Quaternion.Inverse(_bindRotLocal[i]) * _bindDirLocal[i]);
					Quaternion swing = Quaternion.FromToRotation(inheritedAim, aRot * dT.normalized);
					_jointBones[i].rotation = swing * inheritedWorld;
				}
			}
		}

		_bonesAreModified = true;
		return true;
	}

	// Capture the bind (rest) pose references once, from _bonesBackup (cloned untouched at
	// init), expressed in the avatar's local space so the retarget is independent of where
	// the avatar is placed.
	private void _captureRetargetBind(Transform avatarT)
	{
		_jointBones = new Transform[22];
		_bindRotLocal = new Quaternion[22];
		_bindDirLocal = new Vector3[22];
		Vector3[] bindLocalPos = new Vector3[22];
		Quaternion aRotInv = Quaternion.Inverse(avatarT.rotation);

		for (int a = 0; a < _bones.Length; a++)
		{
			string boneName = _bones[a].name.Replace(_boneNamePrefix, "");
			int idx;
			if (_boneNameToJointIndex.TryGetValue(boneName, out idx) && idx < 22)
			{
				_jointBones[idx] = _bones[a];
				bindLocalPos[idx] = avatarT.InverseTransformPoint(_bonesBackup[a].position);
				_bindRotLocal[idx] = aRotInv * _bonesBackup[a].rotation;
			}
		}

		for (int i = 1; i < 22; i++)
		{
			int c = _primaryChild[i];
			if (c >= 0)
				_bindDirLocal[i] = (bindLocalPos[c] - bindLocalPos[i]).normalized;
		}

		Vector3 upB = (bindLocalPos[3] - bindLocalPos[0]).normalized;
		Vector3 rightB = (bindLocalPos[2] - bindLocalPos[1]).normalized;
		_pelvisBindBasis = Quaternion.LookRotation(Vector3.Cross(rightB, upB).normalized, upB);

		// Bind basis for the spine/neck twist chain.
		_isTorso = new bool[22];
		_torsoBlendByJoint = new float[22];
		_torsoBindBasis = new Quaternion[22];
		for (int k = 0; k < _torsoJoints.Length; k++)
		{
			int t = _torsoJoints[k];
			int c = _primaryChild[t];
			_isTorso[t] = true;
			_torsoBlendByJoint[t] = _torsoBlend[k];
			Vector3 up = (bindLocalPos[c] - bindLocalPos[t]).normalized;
			Vector3 right = TorsoRight(bindLocalPos, _torsoBlend[k]);
			_torsoBindBasis[t] = Quaternion.LookRotation(Vector3.Cross(right, up).normalized, up);
		}

		// Hip twist bind basis (legs straight at bind, so anchor the pole to body forward/back).
		_isHipBasis = new bool[22];
		_hipBindBasis = new Quaternion[22];
		Vector3 bindForward = (_pelvisBindBasis * Vector3.forward).normalized;
		for (int h = 1; h <= 2; h++)   // L_Hip, R_Hip
		{
			int c = _primaryChild[h];
			Vector3 aim = bindLocalPos[c] - bindLocalPos[h];
			Quaternion b;
			if (TryAimPoleBasis(aim, HIP_POLE_SIGN * bindForward, out b))
			{
				_isHipBasis[h] = true;
				_hipBindBasis[h] = b;
			}
		}

		_retargetCaptured = true;
	}

	// Full orientation from a bone direction (`aim`) and a bend pole (`pole`): the bone's
	// length axis follows `aim`, twist fixed so the bend plane contains `pole`. Returns
	// false if `pole` is ~parallel to `aim` (straight limb -> twist undefined).
	private static bool TryAimPoleBasis(Vector3 aim, Vector3 pole, out Quaternion basis)
	{
		Vector3 up = aim;
		if (up.sqrMagnitude < 1e-10f) { basis = Quaternion.identity; return false; }
		up.Normalize();
		Vector3 right = Vector3.Cross(pole, up);
		if (right.sqrMagnitude < 1e-6f) { basis = Quaternion.identity; return false; }
		right.Normalize();
		Vector3 fwd = Vector3.Cross(up, right);
		basis = Quaternion.LookRotation(fwd, up);
		return true;
	}

	// Body left/right axis for the torso, blended from the hip line (f=0) to the shoulder
	// line (f=1). Used to give the spine/neck a twist reference. `p` is indexed by joint.
	private static Vector3 TorsoRight(Vector3[] p, float f)
	{
		Vector3 hipR = (p[2] - p[1]).normalized;      // L_Hip -> R_Hip
		Vector3 shoulderR = (p[17] - p[16]).normalized;  // L_Shoulder -> R_Shoulder
		return Vector3.Lerp(hipR, shoulderR, f).normalized;
	}



    private void _cloneBones(Transform[] bonesOriginal, Transform[] bonesModified)
	{
		// Clone transforms (name, position, rotation)
		for (int i=0; i<bonesModified.Length; i++)
		{
			bonesModified[i] = new GameObject().transform;
			bonesModified[i].name = bonesOriginal[i].name + "_clone";
			bonesModified[i].position = bonesOriginal[i].position;
			bonesModified[i].rotation = bonesOriginal[i].rotation;
		}

		// Clone hierarchy
		for (int i=0; i<bonesModified.Length; i++)
		{
			string parentName = bonesOriginal[i].parent.name;

			// Find transform with same name in copy
			GameObject go = GameObject.Find(parentName + "_clone");
			if (go == null)
			{
				// Cannot find parent so must be armature
				bonesModified[i].parent = bonesOriginal[i].parent;
			}
			else
			{
				bonesModified[i].parent = go.transform;
			}

		}

		return;

	}	

	private void _restoreBones()
	{
		// Restore transforms (name, position, rotation)
		for (int i=0; i<_bones.Length; i++)
		{
			_bones[i].position = _bonesBackup[i].position;
			_bones[i].rotation = _bonesBackup[i].rotation;
		}
	}	

	private void _setBindPose(Transform[] bones)
	{
		Matrix4x4[] bindPoses = targetRenderer.sharedMesh.bindposes;
//		Debug.Log("Bind poses: " + bindPoses.Length);

        Transform avatarRootTransform = targetRenderer.transform.parent;

		for (int i=0; i<bones.Length; i++)
		{
	        // The bind pose is bone's inverse transformation matrix.
	        // Make this matrix relative to the avatar root so that we can move the root game object around freely.            
            bindPoses[i] = bones[i].worldToLocalMatrix * avatarRootTransform.localToWorldMatrix;
		}

		targetRenderer.bones = bones;
		Mesh sharedMesh = targetRenderer.sharedMesh;
		sharedMesh.bindposes = bindPoses;
		targetRenderer.sharedMesh = sharedMesh;

        _bonesAreModified = true;
	}

    private void _localBounds(ref Vector3 min, ref Vector3 max)
    {
        targetRenderer.BakeMesh(_bakedMesh);
        Vector3[] vertices = _bakedMesh.vertices;
        int numVertices = vertices.Length;

        float xMin = Mathf.Infinity;
        float xMax = Mathf.NegativeInfinity;
        float yMin = Mathf.Infinity;
        float yMax = Mathf.NegativeInfinity;
        float zMin = Mathf.Infinity;
        float zMax = Mathf.NegativeInfinity;

        for (int i=0; i<numVertices; i++)
        {
            Vector3 v = vertices[i];

            if (v.x < xMin)
            {
                xMin = v.x;
            }
            else if (v.x > xMax)
            {
                xMax = v.x;
            }

            if (v.y < yMin)
            {
                yMin = v.y;
            }
            else if (v.y > yMax)
            {
                yMax = v.y;
            }

            if (v.z < zMin)
            {
                zMin = v.z;
            }
            else if (v.z > zMax)
            {
                zMax = v.z;
            }
        }

        min.x = xMin;
        min.y = yMin;
        min.z = zMin;
        max.x = xMax;
        max.y = yMax;
        max.z = zMax;
//      Debug.Log("MinMax: x[" + xMin + "," + xMax + "], y["  + yMin + "," + yMax + "], z["  + zMin + "," + zMax + "]");
    }

    // Note: Cannot use OnDestroy() because in OnDestroy the bone Transform objects are already destroyed
    //       See also https://docs.unity3d.com/Manual/ExecutionOrder.html
	public void OnApplicationQuit()
	{
		Debug.Log("OnApplicationQuit: Restoring original bind pose");

        if (! _initialized)
            return;

        if (! _bonesAreModified)
            return;

		if ((_bones != null) && (_bonesBackup != null))
		{
			_restoreBones();
			_setBindPose(_bones);
		}
	}
}

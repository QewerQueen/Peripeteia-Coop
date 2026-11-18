using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Remote player visual. We clone the local player's model hierarchy and strip
/// every gameplay component from the clone: it must never read input, run AI,
/// play audio or collide with the world. Transforms, renderers, mesh filters and
/// the game Animator survive. Full body animation is driven by the game's own
/// Animator now that the clone has no gameplay scripts. A procedural walk cycle
/// remains as a fallback (`-coop-procedural-animation`) and as the bone source
/// when an Animator/avatar is unavailable.
/// </summary>
internal sealed class RemoteAvatar
{
    private readonly Dictionary<BoneRole, BoneState> _bones = new();

    private Vector3 _position;
    private float _yaw;
    private bool _usingFallback;
    private float _phase;
    private Transform? _source;
    private string _sourceScene = string.Empty;

    // Root child names the last cloned local body had. A respawn rebuilds the
    // local player from PlayerPrefab, and that fresh body can still be missing a
    // part (the ragdoll/skeleton root) at the moment the clone is taken. Cloning
    // it then produced a remote puppet with limbs missing, so a same-scene
    // rebuild now waits for a body that still carries everything the old one did.
    private string[]? _sourceRootChildren;
    private bool _sourceIncompleteLogged;

    private float _nextRefreshTime;
    private Animator? _gameAnimator;

    private float _vertical;
    private float _horizontal;
    private float _speed;
    private bool _crouch;
    private bool _inAir;
    private bool _jumping;
    private float _lean;
    private bool _dragging;
    private float _leanSmooth;
    private float _dragWeight;
    private float _nextDragLog;

    // Remote weapon visual state. The body clone is made from the local player,
    // so any weapon inside it belongs to the wrong player. It is cleared and
    // replaced by a visual built from the WeaponState packets.
    private GameObject? _weaponVisual;
    private Transform? _weaponMuzzle;
    private Vector3 _weaponBarrelLocalDirection = Vector3.forward;
    private Light? _weaponFlash;
    private Animator? _weaponAnimator;
    private string _weaponDescriptor = string.Empty;
    private string _weaponParentPath = "UpperPivot/WeaponPivot/WeaponSlot";
    private string _weaponMuzzlePath = string.Empty;
    private string _weaponItemName = string.Empty;
    private string _weaponTemplateName = string.Empty;
    private int _weaponVisualHash;
    private Vector3 _weaponLocalPosition;
    private Quaternion _weaponLocalRotation = Quaternion.identity;
    private Vector3 _weaponLocalScale = Vector3.one;
    private Vector3 _weaponWorldScale = Vector3.one;
    private Quaternion _weaponRootRotation = Quaternion.identity;
    private Vector3 _weaponCameraAim = Vector3.zero;
    private bool _weaponUseHandAnchor;
    private Transform? _weaponHand;
    private Transform? _weaponRightGrab;
    private Transform? _weaponLeftGrab;
    private Vector3 _weaponGripLocalPosition;
    private Quaternion _weaponGripLocalRotation = Quaternion.identity;
    private Vector3 _weaponHandGripLocalPosition;
    private Quaternion _weaponHandGripLocalRotation = Quaternion.identity;
    private bool _weaponHasHandGrip;
    private bool _weaponPlacementLogged;
    private bool _weaponHoldLogged;
    private Vector3 _lastWeaponHoldTarget;
    private Vector3 _lastWeaponAimDirection;
    private float _lastWeaponAimPitch;
    private Vector3 _weaponBoundsCenterLocal;
    private float _weaponProbeUntil;
    private float _nextWeaponProbeTime;
    private string _weaponRightGrabPath = string.Empty;
    private string _weaponLeftGrabPath = string.Empty;
    private bool _weaponOneHanded;
    private Vector3 _weaponHandRotationMod;
    private RemoteWeaponIK? _weaponIk;
    private bool _weaponVisible;
    private float _weaponHoldWeight;
    private float _nextWeaponBuildTime;
    private int _weaponAnimatorHash;
    private float _weaponAnimatorTime;
    private bool _weaponAnimatorActive;
    private int _appliedWeaponAnimatorHash;
    private float _appliedWeaponAnimatorTime;
    private float _weaponFlashUntil;

    // Player-versus-player physics proxy (body capsule + head sphere). It is a
    // child of the current root, so it is rebuilt together with the visual.
    private RemoteHitProxy? _hitProxy;

    private int _actionHash;
    private float _actionTime;
    private bool _actionActive;
    private int _appliedActionHash;
    private float _appliedActionTime;

    // The player model ships with a baked hitbox ragdoll (Rigidbody + Collider +
    // CharacterJoint per limb). The clone keeps that rig but inert so a remote
    // death can collapse the body. See CollectRagdollRig / EnterRagdoll.
    private readonly List<Rigidbody> _ragdollBodies = new();
    private readonly List<Collider> _ragdollColliders = new();
    private readonly List<CharacterJoint> _ragdollJoints = new();
    private readonly List<int> _ragdollOriginalLayers = new();
    private readonly List<string> _ragdollInfo = new();
    private int _ragdollLayer = -1;
    private int _ragdollProbeLayer = -1;
    private bool _dead;
    private bool _ragdollActive;

    // A simulated ragdoll keeps sliding for a moment (the limb colliders push
    // each other apart) and the teammate can bump it, which looked like the
    // corpse crawling around. Once the fall has settled the bodies are pinned
    // so the body stays exactly where it fell.
    private const float RagdollMinSettle = 0.7f;
    private const float RagdollMaxSettle = 3.0f;
    private bool _ragdollFrozen;
    private float _ragdollAge;

    // A death can arrive before the puppet exists (the avatar is created a beat
    // after the first packets). Keep the pose so the ragdoll can still be built
    // once the visual shows up.
    private bool _pendingDeathPose;
    private Vector3 _pendingDeathPosition;
    private Quaternion _pendingDeathRotation;

    private enum BoneRole
    {
        Hips,
        Spine,
        Chest,
        LeftUpperLeg,
        RightUpperLeg,
        LeftLowerLeg,
        RightLowerLeg,
        LeftUpperArm,
        RightUpperArm,
        LeftLowerArm,
        RightLowerArm,
        LeftHand,
        RightHand,
    }

    private readonly struct BoneState
    {
        public BoneState(Transform bone, Quaternion baseRotation)
        {
            Bone = bone;
            BaseRotation = baseRotation;
        }

        public Transform Bone { get; }

        public Quaternion BaseRotation { get; }
    }

    public RemoteAvatar(int id, string name, Vector3 position, float yaw, Transform? source)
    {
        Id = id;
        Name = name;
        TargetPosition = position;
        TargetYaw = yaw;
        _position = position;
        _yaw = yaw;

        Root = CreateVisual(source, out bool cloned);
        _usingFallback = !cloned;
        _source = cloned ? source : null;
        _sourceScene = cloned && source != null ? source.gameObject.scene.name : string.Empty;
        _sourceRootChildren = cloned && source != null ? RootChildNames(source) : null;
        Root.transform.position = position;
        Root.transform.rotation = Quaternion.Euler(0.0f, yaw, 0.0f);

        UnityEngine.Object.DontDestroyOnLoad(Root);
    }

    public int Id { get; }

    public string Name { get; set; }

    public GameObject Root { get; private set; }

    /// <summary>Physics proxy that makes this puppet hittable and solid.</summary>
    public RemoteHitProxy? HitProxy => _hitProxy;

    public Vector3 TargetPosition { get; set; }

    public float TargetYaw { get; set; }

    public bool HasPose { get; private set; }

    /// <summary>True while the remote player is dead and the body is down.</summary>
    public bool IsDead => _dead;

    /// <summary>
    /// True after the remote player has touched the ground at least once in
    /// the current scene. Spawn points can be high above the map while the save
    /// is still loading; showing the puppet immediately makes it look like the
    /// other player is floating in the sky.
    /// </summary>
    public bool HasLanded { get; private set; }

    public string PoseScene { get; private set; } = string.Empty;

    public string RemoteScene { get; private set; } = string.Empty;

    public bool RemoteReady { get; private set; }

    public void SetTarget(
        Vector3 position,
        float yaw,
        string? poseScene = null,
        bool grounded = false
    )
    {
        TargetPosition = position;
        TargetYaw = yaw;
        HasPose = true;
        if (grounded)
        {
            HasLanded = true;
        }

        if (poseScene != null)
        {
            PoseScene = poseScene;
        }
    }

    public void SetLevel(string sceneName, bool ready)
    {
        string newScene = sceneName ?? string.Empty;
        bool sceneChanged = !string.Equals(RemoteScene, newScene, StringComparison.Ordinal);
        if (sceneChanged || RemoteReady != ready)
        {
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] id={Id} level scene='{newScene}' ready={ready}"
            );
        }

        if (sceneChanged || (RemoteReady && !ready))
        {
            // A new scene or a return from the menu may start with an
            // intermediate spawn high above the floor. Wait for a grounded
            // pose instead of showing that position.
            HasLanded = false;
            HasPose = false;
            PoseScene = string.Empty;
        }

        RemoteScene = newScene;
        RemoteReady = ready;
    }

    /// <summary>
    /// Hides the puppet while the remote player is still in the menu/loading
    /// screen. Otherwise the host sees a capsule far away "in the air" until
    /// the other instance finishes loading the same level.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (Root == null)
        {
            return;
        }

        // Never show a puppet that has not sent a pose yet. Its initial
        // transform is (0,0,0), which looks like a character floating in the
        // sky before the first packet arrives.
        if (visible && !HasPose)
        {
            visible = false;
        }

        if (Root.activeSelf == visible)
        {
            return;
        }

        Root.SetActive(visible);
        CoopPlugin.LogSource.LogInfo(
            $"[avatar] id={Id} visible={visible} scene='{RemoteScene}' ready={RemoteReady}"
        );
        if (visible)
        {
            _position = TargetPosition;
            _yaw = TargetYaw;
            Root.transform.position = _position;
            Root.transform.rotation = Quaternion.Euler(0.0f, _yaw, 0.0f);
        }
    }

    /// <summary>
    /// Rebuilds the remote model when the local player enters a real level. A
    /// connection made from the main menu used to keep the menu model forever,
    /// which looked like a half-body puppet in the match. The menu model is not
    /// cloned at all now; a capsule is shown until the in-level player exists.
    /// </summary>
    public void TryRefresh(Transform? source)
    {
        if (source == null || Root == null)
        {
            return;
        }

        if (Time.unscaledTime < _nextRefreshTime)
        {
            return;
        }

        string sceneName = source.gameObject.scene.name;
        if (IsMenuScene(sceneName))
        {
            return;
        }

        bool sceneChanged = !string.Equals(_sourceScene, sceneName, StringComparison.Ordinal);
        bool sourceChanged = _source != source;
        if (!_usingFallback && !sceneChanged && !sourceChanged)
        {
            return;
        }

        // A same-scene body swap is the local player's respawn: the game rebuilds
        // the player from PlayerPrefab, and for a moment that new body can be
        // missing a part the old one had. Cloning it then made the teammate's
        // puppet lose limbs, so keep the clone we already have until the new body
        // is whole again. A real scene change always rebuilds.
        if (!_usingFallback && !sceneChanged && sourceChanged && _source != null &&
            !SourceMatchesClone(source))
        {
            LogIncompleteSource(source);
            return;
        }

        _nextRefreshTime = Time.unscaledTime + 1.0f;

        try
        {
            GameObject rebuilt = ClonePlayerModel(source);
            rebuilt.transform.position = Root.transform.position;
            rebuilt.transform.rotation = Root.transform.rotation;
            UnityEngine.Object.DontDestroyOnLoad(rebuilt);

            GameObject oldRoot = Root;
            Root = rebuilt;
            UnityEngine.Object.Destroy(oldRoot);

            _usingFallback = false;
            _source = source;
            _sourceScene = sceneName;
            _sourceRootChildren = RootChildNames(source);
            _sourceIncompleteLogged = false;

            // The old weapon visual was a child of the old root and has just
            // been destroyed with it. Recreate it from the last state packet.
            _weaponVisual = null;
            _weaponMuzzle = null;
            _weaponFlash = null;
            _weaponAnimator = null;
            _weaponHand = null;
            _weaponRightGrab = null;
            _weaponLeftGrab = null;
            _weaponHasHandGrip = false;
            _weaponIk = null;
            _nextWeaponBuildTime = 0.0f;
            if (_weaponVisible)
            {
                TryBuildWeaponVisual();
                ApplyWeaponTransform();
            }

            CoopPlugin.LogSource.LogInfo(
                $"[avatar] rebuilt avatar id={Id} for scene='{sceneName}'."
            );

            // A rebuilt model gets a fresh, inert ragdoll rig. If the remote
            // player is dead, put the new body back down.
            if (_dead)
            {
                _dead = false;
                SetDead(true, Root.transform.position, Root.transform.rotation);
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[avatar] failed to rebuild avatar id={Id}: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static bool IsMenuScene(string sceneName)
    {
        return !string.IsNullOrEmpty(sceneName) &&
               sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Names of the direct children of the local player root.</summary>
    private static string[] RootChildNames(Transform source)
    {
        int count = source.childCount;
        string[] names = new string[count];
        for (int i = 0; i < count; i++)
        {
            Transform child = source.GetChild(i);
            names[i] = child != null ? child.name : string.Empty;
        }

        return names;
    }

    /// <summary>
    /// True when the current local body still carries every root child the body
    /// we cloned from had. A freshly revived body can briefly be missing one, and
    /// cloning that produced a puppet with limbs missing.
    /// </summary>
    private bool SourceMatchesClone(Transform source)
    {
        string[]? names = _sourceRootChildren;
        if (names == null || names.Length == 0)
        {
            return true;
        }

        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (name.Length > 0 && source.Find(name) == null)
            {
                return false;
            }
        }

        return true;
    }

    private void LogIncompleteSource(Transform source)
    {
        if (_sourceIncompleteLogged)
        {
            return;
        }

        _sourceIncompleteLogged = true;

        List<string> missing = new();
        string[]? names = _sourceRootChildren;
        if (names != null)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].Length > 0 && source.Find(names[i]) == null)
                {
                    missing.Add(names[i]);
                }
            }
        }

        CoopPlugin.LogSource.LogInfo(
            $"[avatar] keeping avatar id={Id}: the new local body is still missing " +
            $"'{string.Join(", ", missing)}' (roots {names?.Length ?? 0} -> {source.childCount})."
        );
    }

    public void TryUpgrade(Transform? source)
    {
        TryRefresh(source);
    }

    /// <summary>
    /// Stores the same animator parameters that PlayerMovement drives locally.
    /// They drive the full game Animator, or the procedural fallback when full
    /// animation is disabled or no humanoid avatar is available.
    /// </summary>
    public void SetAnimation(
        float vertical,
        float horizontal,
        float speed,
        float crouchTimer,
        bool crouch,
        bool sprint,
        bool inAir,
        bool jumping,
        bool shimmy)
    {
        _vertical = vertical;
        _horizontal = horizontal;
        _speed = speed;
        _crouch = crouch;
        _inAir = inAir;
        _jumping = jumping;

        ApplyGameAnimatorParameters(vertical, horizontal, speed, crouchTimer, crouch, sprint, inAir, jumping, shimmy);
    }

    /// <summary>
    /// Extra third-person poses that the game drives with IK rather than
    /// Animator parameters. The clone has no gameplay scripts, so they are
    /// reconstructed here as additive bone offsets after the Animator update.
    /// </summary>
    public void SetPoseExtras(float lean, bool dragging)
    {
        _lean = Mathf.Clamp(lean, -45.0f, 45.0f);
        if (dragging != _dragging)
        {
            _dragging = dragging;
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] id={Id} dragging={(dragging ? "on" : "off")}"
            );
        }

        UpdateWeaponIk();
    }

    /// <summary>
    /// Receives the remote player's equipped weapon: descriptor, local hand
    /// transform and optional weapon Animator state.
    /// </summary>
    public void SetWeaponState(WeaponSnapshot snapshot)
    {
        _weaponVisible = snapshot.Visible;
        if (!snapshot.Visible)
        {
            _weaponDescriptor = string.Empty;
            _weaponItemName = string.Empty;
            _weaponHasHandGrip = false;
            _weaponCameraAim = Vector3.zero;
            DestroyWeaponVisual();
            return;
        }

        bool descriptorChanged = !string.Equals(
            _weaponDescriptor,
            snapshot.Descriptor,
            StringComparison.Ordinal
        );

        _weaponItemName = snapshot.ItemName;
        _weaponTemplateName = snapshot.TemplateName;
        _weaponParentPath = string.IsNullOrEmpty(snapshot.ParentPath)
            ? _weaponParentPath
            : snapshot.ParentPath;
        _weaponMuzzlePath = snapshot.MuzzlePath;
        _weaponVisualHash = snapshot.VisualHash;
        _weaponLocalPosition = snapshot.LocalPosition;
        _weaponLocalRotation = snapshot.LocalRotation;
        _weaponLocalScale = snapshot.LocalScale;
        _weaponWorldScale = snapshot.LossyScale;
        _weaponRootRotation = snapshot.RootRotation;
        _weaponCameraAim = snapshot.CameraAim;
        _weaponHandGripLocalPosition = snapshot.HandGripLocalPosition;
        _weaponHandGripLocalRotation = snapshot.HandGripLocalRotation;
        _weaponHasHandGrip = snapshot.HasHandGrip;
        _weaponRightGrabPath = snapshot.RightGrabPath;
        _weaponLeftGrabPath = snapshot.LeftGrabPath;
        _weaponOneHanded = snapshot.OneHanded;
        _weaponHandRotationMod = snapshot.HandRotationMod;
        _weaponAnimatorHash = snapshot.AnimatorHash;
        _weaponAnimatorTime = snapshot.AnimatorTime;
        _weaponAnimatorActive = snapshot.AnimatorActive;

        if (descriptorChanged)
        {
            _weaponDescriptor = snapshot.Descriptor;
            _nextWeaponBuildTime = 0.0f;
        }

        if (_weaponVisual == null || descriptorChanged)
        {
            TryBuildWeaponVisual();

            // Building the visual destroys the previous one, which clears the
            // hand-grip state. Restore the values from this packet before the
            // transform is applied.
            _weaponHandGripLocalPosition = snapshot.HandGripLocalPosition;
            _weaponHandGripLocalRotation = snapshot.HandGripLocalRotation;
            _weaponHasHandGrip = snapshot.HasHandGrip;
        }

        _weaponIk?.SetHandRotationMod(_weaponHandRotationMod);
        UpdateWeaponIk();
        ApplyWeaponTransform();
    }

    public void SetActionState(int stateHash, float normalizedTime, bool active)
    {
        _actionHash = stateHash;
        _actionTime = normalizedTime;
        _actionActive = active && stateHash != 0;
        UpdateWeaponIk();
    }

    public void ApplyWeaponEvent(WeaponEvent weaponEvent)
    {
        if (weaponEvent != WeaponEvent.Shot)
        {
            return;
        }

        _weaponFlashUntil = Time.unscaledTime + 0.055f;
        if (_weaponFlash == null && _weaponMuzzle != null)
        {
            GameObject flashObject = new GameObject("PeripeteiaCoop_MuzzleFlash");
            flashObject.transform.SetParent(_weaponMuzzle, false);
            _weaponFlash = flashObject.AddComponent<Light>();
            _weaponFlash.type = LightType.Point;
            _weaponFlash.range = 4.0f;
            _weaponFlash.color = new Color(1.0f, 0.72f, 0.32f);
            _weaponFlash.shadows = LightShadows.None;
        }

        if (_weaponFlash != null)
        {
            _weaponFlash.enabled = true;
            _weaponFlash.intensity = 5.0f;
        }
    }

    /// <summary>
    /// Called from LateUpdate, after the game Animator has written the current
    /// locomotion pose. Lean (Q/E) and the dragging hand pose are additive, so
    /// they do not destroy the full-body animation.
    /// </summary>
    public void ApplyLatePose()
    {
        if (Root == null || !Root.activeInHierarchy || _dead)
        {
            return;
        }

        float deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);

        // Pause managers can disable animators after Update. Re-assert this in
        // LateUpdate, after the game's pause scripts, so the remote body keeps
        // moving even while the local player has the pause menu open.
        if (_gameAnimator != null && Time.timeScale <= 0.001f)
        {
            if (!_gameAnimator.enabled)
            {
                _gameAnimator.enabled = true;
            }

            if (_gameAnimator.updateMode != AnimatorUpdateMode.UnscaledTime)
            {
                _gameAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
            }
        }

        if (_bones.Count > 0)
        {
            float targetLean = Mathf.Clamp(_lean, -45.0f, 45.0f);
            _leanSmooth = Mathf.Lerp(_leanSmooth, targetLean, 1.0f - Mathf.Exp(-14.0f * deltaTime));

            float dragTarget = _dragging ? 1.0f : 0.0f;
            _dragWeight = Mathf.MoveTowards(_dragWeight, dragTarget, deltaTime * 8.0f);

            if (Mathf.Abs(_leanSmooth) > 0.1f)
            {
                ApplyAdditive(
                    BoneRole.Spine,
                    Quaternion.Euler(0.0f, 0.0f, -_leanSmooth * 0.55f)
                );
                ApplyAdditive(
                    BoneRole.Chest,
                    Quaternion.Euler(0.0f, 0.0f, -_leanSmooth * 0.45f)
                );
            }

            if (_dragWeight > 0.01f)
            {
                ApplyDragHand(_dragWeight);
            }
        }

        ApplyRemoteAction();

        // While the remote avatar is idle with a weapon, bend the right arm into
        // a simple holding pose. The full-body Animator still owns everything
        // else and action clips (fire/reload/swing) are left untouched so their
        // original arm animation remains visible.
        float holdTarget = _weaponVisible &&
                           _weaponVisual != null &&
                           !_actionActive &&
                           !_usingFallback
            ? 1.0f
            : 0.0f;
        _weaponHoldWeight = Mathf.MoveTowards(
            _weaponHoldWeight,
            holdTarget,
            deltaTime * 6.0f
        );
        if (_weaponHoldWeight > 0.01f)
        {
            ApplyWeaponHoldHand(_weaponHoldWeight);
        }

        UpdateWeaponIk();
        ApplyWeaponPose();
        ApplyWeaponSupportHand(_weaponHoldWeight);
        LogWeaponProbe();
    }

    private void ApplyWeaponSupportHand(float weight)
    {
        if (!_weaponVisible ||
            _weaponOneHanded ||
            _weaponLeftGrab == null ||
            _actionActive ||
            _dragging ||
            weight <= 0.01f ||
            Root == null ||
            !_bones.TryGetValue(BoneRole.LeftUpperArm, out BoneState upper) ||
            !_bones.TryGetValue(BoneRole.LeftLowerArm, out BoneState lower) ||
            !_bones.TryGetValue(BoneRole.LeftHand, out BoneState hand) ||
            upper.Bone == null ||
            lower.Bone == null ||
            hand.Bone == null)
        {
            return;
        }

        Vector3 shoulder = upper.Bone.position;
        Vector3 target = _weaponLeftGrab.position;
        Vector3 leftPole = shoulder -
                           Root.transform.right * 0.25f +
                           Root.transform.forward * 0.05f -
                           Vector3.up * 0.20f;
        SolveTwoBone(
            upper.Bone,
            lower.Bone,
            hand.Bone,
            target,
            leftPole,
            weight
        );
        hand.Bone.rotation = Quaternion.Slerp(
            hand.Bone.rotation,
            _weaponLeftGrab.rotation * Quaternion.Euler(_weaponHandRotationMod),
            Mathf.Clamp01(weight)
        );
    }

    private void LogWeaponProbe()
    {
        if (!_weaponVisible ||
            _weaponVisual == null ||
            Time.unscaledTime >= _weaponProbeUntil ||
            Time.unscaledTime < _nextWeaponProbeTime)
        {
            return;
        }

        _nextWeaponProbeTime = Time.unscaledTime + 0.5f;
        Vector3 rightHand = _bones.TryGetValue(BoneRole.RightHand, out BoneState right) &&
                            right.Bone != null
            ? right.Bone.position
            : Vector3.zero;
        Vector3 leftHand = _bones.TryGetValue(BoneRole.LeftHand, out BoneState left) &&
                           left.Bone != null
            ? left.Bone.position
            : Vector3.zero;
        Vector3 leftUpper = _bones.TryGetValue(BoneRole.LeftUpperArm, out BoneState upperArm) &&
                            upperArm.Bone != null
            ? upperArm.Bone.position
            : Vector3.zero;
        Vector3 leftLower = _bones.TryGetValue(BoneRole.LeftLowerArm, out BoneState lowerArm) &&
                            lowerArm.Bone != null
            ? lowerArm.Bone.position
            : Vector3.zero;
        Transform? animatorLeft = _gameAnimator != null &&
                                  _gameAnimator.isHuman
            ? _gameAnimator.GetBoneTransform(HumanBodyBones.LeftHand)
            : null;
        Transform? head = _gameAnimator != null && _gameAnimator.isHuman
            ? _gameAnimator.GetBoneTransform(HumanBodyBones.Head)
            : null;
        Vector3 grip = _weaponRightGrab != null
            ? _weaponRightGrab.position
            : Vector3.zero;
        Vector3 leftGrab = _weaponLeftGrab != null
            ? _weaponLeftGrab.position
            : Vector3.zero;
        Vector3 weaponCenter = _weaponVisual.transform.TransformPoint(
            _weaponBoundsCenterLocal
        );
        Vector3 barrelWorld = _weaponVisual.transform.rotation *
                              _weaponBarrelLocalDirection;
        Vector3 muzzleWorld = _weaponMuzzle != null
            ? _weaponMuzzle.position
            : Vector3.zero;
        CoopPlugin.LogSource.LogInfo(
            $"[avatar] id={Id} weapon probe hold={_weaponHoldWeight:F2} " +
            $"ikWeight={(_weaponIk != null ? _weaponIk.CurrentWeight : 0.0f):F2} " +
            $"pitch={_lastWeaponAimPitch:F2} aim={_lastWeaponAimDirection} " +
            $"barrelLocal={_weaponBarrelLocalDirection} barrelWorld={barrelWorld} " +
            $"muzzleWorld={muzzleWorld} " +
            $"target={_lastWeaponHoldTarget} " +
            $"rightHand={rightHand} rightDistance={Vector3.Distance(rightHand, _lastWeaponHoldTarget):F3} " +
            $"leftHand={leftHand} " +
            $"leftUpper={leftUpper} leftLower={leftLower} " +
            $"ikLeftHand={(animatorLeft != null ? animatorLeft.position : Vector3.zero)} " +
            $"leftGrab={leftGrab} leftDistance={Vector3.Distance(leftHand, leftGrab):F3} " +
            $"ikLeftDistance={(animatorLeft != null ? Vector3.Distance(animatorLeft.position, leftGrab) : -1.0f):F3} " +
            $"grip={grip} weaponCenter={weaponCenter} " +
            $"head={(head != null ? head.position : Vector3.zero)}."
        );
    }

    private void UpdateWeaponIk()
    {
        if (_weaponIk == null)
        {
            return;
        }

        bool active = _weaponVisible &&
                      _weaponVisual != null &&
                      !_weaponOneHanded &&
                      _weaponLeftGrab != null &&
                      !_actionActive &&
                      !_dragging &&
                      _weaponHoldWeight > 0.35f;
        _weaponIk.SetLeftGrab(_weaponLeftGrab, active);
    }

    private void ApplyRemoteAction()
    {
        if (!_actionActive || _gameAnimator == null)
        {
            return;
        }

        float time = Mathf.Repeat(_actionTime, 1.0f);
        bool sameState = _appliedActionHash == _actionHash;
        if (sameState && Mathf.Abs(time - _appliedActionTime) < 0.18f)
        {
            return;
        }

        try
        {
            _gameAnimator.Play(_actionHash, 0, time);
            _appliedActionHash = _actionHash;
            _appliedActionTime = time;
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[avatar] action Animator.Play failed: {exception.GetType().Name}: {exception.Message}"
            );
            _actionActive = false;
        }
    }

    private void ApplyWeaponPose()
    {
        if (_weaponVisible && _weaponVisual == null)
        {
            TryBuildWeaponVisual();
        }

        ApplyWeaponHandRotation();
        ApplyWeaponTransform();

        if (_weaponAnimator != null && _weaponAnimatorActive)
        {
            float time = Mathf.Repeat(_weaponAnimatorTime, 1.0f);
            bool sameState = _appliedWeaponAnimatorHash == _weaponAnimatorHash;
            if (!sameState || Mathf.Abs(time - _appliedWeaponAnimatorTime) > 0.2f)
            {
                try
                {
                    _weaponAnimator.enabled = true;
                    _weaponAnimator.Play(_weaponAnimatorHash, 0, time);
                    _appliedWeaponAnimatorHash = _weaponAnimatorHash;
                    _appliedWeaponAnimatorTime = time;
                }
                catch (Exception exception)
                {
                    CoopPlugin.LogSource.LogWarning(
                        $"[avatar] weapon Animator.Play failed: {exception.GetType().Name}: {exception.Message}"
                    );
                    _weaponAnimatorActive = false;
                }
            }
        }

        if (_weaponFlash != null && Time.unscaledTime >= _weaponFlashUntil)
        {
            _weaponFlash.intensity = 0.0f;
            _weaponFlash.enabled = false;
        }
    }

    private void TryBuildWeaponVisual()
    {
        if (!_weaponVisible || Root == null || Time.unscaledTime < _nextWeaponBuildTime)
        {
            return;
        }

        _nextWeaponBuildTime = Time.unscaledTime + 1.0f;

        GameObject? visual = WeaponSync.CreateWeaponVisual(
            _weaponItemName,
            _weaponTemplateName,
            _weaponVisualHash
        );
        if (visual == null)
        {
            if (CoopPlugin.ProbeEnabled)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[avatar] id={Id} waiting for weapon template " +
                    $"'{_weaponItemName}' / '{_weaponTemplateName}'."
                );
            }

            return;
        }

        // WeaponLocation is a first-person helper: the game moves it near the
        // camera/first-person arms, not near the third-person skeleton. The
        // cloned body has no first-person scripts, so a weapon parented there
        // ends up behind the character. Put it on the actual right-hand bone;
        // the aim rotation still comes from the sender so the gun points the
        // same way.
        Transform? hand = null;
        if (_bones.TryGetValue(BoneRole.RightHand, out BoneState rightHand))
        {
            hand = rightHand.Bone;
        }

        bool useHandAnchor = hand != null;
        Transform? parent = hand;
        if (parent == null)
        {
            parent = FindTransformByPath(Root.transform, _weaponParentPath);
        }

        if (parent == null)
        {
            parent = Root.transform.Find("UpperPivot/WeaponPivot/WeaponSlot");
        }

        if (parent == null)
        {
            parent = Root.transform.Find("UpperPivot/WeaponPivot");
        }

        if (parent == null)
        {
            parent = Root.transform.Find("WeaponSlot");
        }

        if (parent == null)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[avatar] id={Id} has no weapon slot for '{_weaponItemName}'."
            );
            UnityEngine.Object.Destroy(visual);
            return;
        }

        DestroyWeaponVisual();
        if (!useHandAnchor)
        {
            ClearSlotChildren(parent);
        }

        _weaponPlacementLogged = false;
        _weaponHoldLogged = false;
        visual.transform.SetParent(parent, false);
        _weaponVisual = visual;
        _weaponHand = useHandAnchor ? hand : null;
        _weaponUseHandAnchor = useHandAnchor;
        _weaponVisual.transform.localPosition = _weaponLocalPosition;
        _weaponVisual.transform.localRotation = _weaponLocalRotation;
        ApplyWeaponScale(_weaponVisual.transform);

        _weaponRightGrab = FindGrabPoint(visual.transform, _weaponRightGrabPath, true);
        _weaponLeftGrab = FindGrabPoint(visual.transform, _weaponLeftGrabPath, false);
        Vector3 leftGrabLocal = Vector3.zero;
        if (_weaponLeftGrab != null)
        {
            leftGrabLocal = visual.transform.InverseTransformPoint(
                _weaponLeftGrab.position
            );
        }
        _weaponGripLocalPosition = Vector3.zero;
        _weaponGripLocalRotation = Quaternion.identity;
        if (_weaponRightGrab != null)
        {
            // The game's weapon root is usually parented to WeaponLocation, not
            // to the hand, so its localPosition is not the hand attachment. The
            // real grip point is stored on the weapon itself; its local offset
            // is what we need to put the grip into the remote palm.
            _weaponGripLocalPosition = visual.transform.InverseTransformPoint(
                _weaponRightGrab.position
            );
            _weaponGripLocalRotation =
                Quaternion.Inverse(visual.transform.rotation) *
                _weaponRightGrab.rotation;
        }

        EnsureWeaponIk();

        _weaponMuzzle = FindWeaponMuzzle(visual.transform, _weaponMuzzlePath);
        _weaponBarrelLocalDirection = FindBarrelLocalDirection(
            visual.transform,
            _weaponMuzzle,
            _weaponGripLocalPosition
        );
        _weaponFlash = null;
        _weaponAnimator = null;
        foreach (Animator animator in visual.GetComponentsInChildren<Animator>(true))
        {
            if (animator == null)
            {
                continue;
            }

            if (_weaponAnimator == null && animator.runtimeAnimatorController != null)
            {
                _weaponAnimator = animator;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                animator.enabled = false;
            }
            else
            {
                animator.enabled = false;
            }
        }

        _appliedWeaponAnimatorHash = 0;
        _appliedWeaponAnimatorTime = 0.0f;

        UpdateWeaponIk();

        Bounds weaponBounds = default;
        bool hasWeaponBounds = false;
        var weaponRendererNames = new List<string>();
        foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
            {
                continue;
            }

            weaponRendererNames.Add(
                $"{renderer.name}{(renderer.enabled ? "" : "(off)")}"
            );
            if (!renderer.enabled)
            {
                continue;
            }

            if (!hasWeaponBounds)
            {
                weaponBounds = renderer.bounds;
                hasWeaponBounds = true;
            }
            else
            {
                weaponBounds.Encapsulate(renderer.bounds);
            }
        }

        Vector3 boundsCenterLocal = hasWeaponBounds
            ? visual.transform.InverseTransformPoint(weaponBounds.center)
            : Vector3.zero;
        Vector3 boundsSizeLocal = hasWeaponBounds
            ? visual.transform.InverseTransformVector(weaponBounds.size)
            : Vector3.zero;
        _weaponBoundsCenterLocal = boundsCenterLocal;
        _weaponProbeUntil = Time.unscaledTime + 60.0f;
        _nextWeaponProbeTime = 0.0f;

        CoopPlugin.LogSource.LogInfo(
            $"[avatar] id={Id} weapon visual '{_weaponItemName}' slot='{parent.name}' " +
            $"parentPath='{_weaponParentPath}' " +
            $"anchor={(useHandAnchor ? "hand" : "path")} " +
            $"rightGrab={(_weaponRightGrab != null ? _weaponRightGrab.name : "<none>")} " +
            $"leftGrab={(_weaponLeftGrab != null ? _weaponLeftGrab.name : "<none>")} " +
            $"rightGrabPath='{_weaponRightGrabPath}' " +
            $"leftGrabPath='{_weaponLeftGrabPath}' " +
            $"leftGrabLocal={leftGrabLocal} " +
            $"grabDistance={(_weaponRightGrab != null && _weaponLeftGrab != null ? Vector3.Distance(_weaponRightGrab.position, _weaponLeftGrab.position) : -1.0f):F3} " +
            $"oneHanded={_weaponOneHanded} " +
            $"handGrip={_weaponHandGripLocalPosition} " +
            $"handGripRot={_weaponHandGripLocalRotation.eulerAngles} " +
            $"hasHandGrip={_weaponHasHandGrip} " +
            $"gripLocal={_weaponGripLocalPosition} " +
            $"gripLocalRot={_weaponGripLocalRotation.eulerAngles} " +
            $"localPos={_weaponLocalPosition} localRot={_weaponLocalRotation.eulerAngles} " +
            $"gripDown={CoopPlugin.WeaponGripDown:F3} " +
            $"gripForward={CoopPlugin.WeaponGripForward:F3} " +
            $"weaponWorld={_weaponVisual.transform.position} " +
            $"handWorld={(hand != null ? hand.position : Vector3.zero)} " +
            $"muzzle={(_weaponMuzzle != null ? _weaponMuzzle.name : "<none>")} " +
            $"barrelLocal={_weaponBarrelLocalDirection} " +
            $"worldScale={_weaponVisual.transform.lossyScale} " +
            $"senderWorldScale={_weaponWorldScale} " +
            $"boundsCenterLocal={boundsCenterLocal} " +
            $"boundsSizeLocal={boundsSizeLocal} " +
            $"renderers='{string.Join("|", weaponRendererNames)}' " +
            $"animator={(_weaponAnimator != null ? _weaponAnimator.name : "<none>")}."
        );
    }

    private void EnsureWeaponIk()
    {
        if (_gameAnimator == null)
        {
            return;
        }

        if (_weaponIk != null && _weaponIk.gameObject != null)
        {
            _weaponIk.Configure(_gameAnimator, _weaponHandRotationMod);
            return;
        }

        try
        {
            _weaponIk = _gameAnimator.gameObject.GetComponent<RemoteWeaponIK>();
            if (_weaponIk == null)
            {
                _weaponIk = _gameAnimator.gameObject.AddComponent<RemoteWeaponIK>();
            }

            _weaponIk.Configure(_gameAnimator, _weaponHandRotationMod);
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] id={Id} weapon hand IK attached to '{_gameAnimator.name}'."
            );
        }
        catch (Exception exception)
        {
            _weaponIk = null;
            CoopPlugin.LogSource.LogWarning(
                $"[avatar] id={Id} could not attach weapon hand IK: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static Vector3 GetWeaponGripNudge()
    {
        // Optional manual nudge in the hand's local space. Defaults to zero:
        // the exact palm position comes from the sender's real GrabPoint.
        return new Vector3(
            0.0f,
            -CoopPlugin.WeaponGripDown,
            CoopPlugin.WeaponGripForward
        );
    }

    private Quaternion GetRemoteWeaponRotation()
    {
        Quaternion rotation = Root != null
            ? Root.transform.rotation * _weaponRootRotation
            : _weaponRootRotation;

        if (_weaponCameraAim.sqrMagnitude < 0.0001f)
        {
            return rotation;
        }

        Vector3 barrel = rotation * _weaponBarrelLocalDirection;
        if (barrel.sqrMagnitude < 0.0001f)
        {
            return rotation;
        }

        // Point the barrel along the sender's camera aim. The first-person
        // weapon mesh has its own "ready" angle; copying that angle onto the
        // third-person body makes the pistol point up into the face even when
        // the sender is looking straight ahead.
        Vector3 aim = _weaponCameraAim.normalized;
        return Quaternion.FromToRotation(barrel.normalized, aim) * rotation;
    }

    private void ApplyWeaponHandRotation()
    {
        if (!_weaponUseHandAnchor ||
            _weaponHand == null ||
            Root == null ||
            !_weaponHasHandGrip ||
            _weaponHoldWeight < 0.35f)
        {
            return;
        }

        float gripLength = _weaponHandGripLocalPosition.magnitude;
        if (gripLength > 0.12f)
        {
            return;
        }

        // The sender sends the real hand-to-GrabPoint rotation. Apply it so the
        // remote wrist wraps the grip in the same way as the original IK.
        Quaternion weaponRotation = GetRemoteWeaponRotation();
        Quaternion gripWorldRotation = weaponRotation * _weaponGripLocalRotation;
        Quaternion handRotation =
            gripWorldRotation * Quaternion.Inverse(_weaponHandGripLocalRotation);
        _weaponHand.rotation = Quaternion.Slerp(
            _weaponHand.rotation,
            handRotation,
            Mathf.Clamp01(_weaponHoldWeight)
        );
    }

    private static Vector3 GetFallbackHandGrip(Transform hand)
    {
        // If the rig has finger bones, their base gives us the real palm
        // direction instead of guessing the hand bone's local axis.
        for (int index = 0; index < hand.childCount; index++)
        {
            Transform child = hand.GetChild(index);
            if (child == null)
            {
                continue;
            }

            string name = child.name.ToLowerInvariant();
            if (!name.Contains("index") &&
                !name.Contains("middle") &&
                !name.Contains("ring") &&
                !name.Contains("pinky") &&
                !name.Contains("thumb") &&
                !name.Contains("finger"))
            {
                continue;
            }

            Vector3 local = hand.InverseTransformPoint(child.position);
            float length = local.magnitude;
            if (length > 0.002f && length < 0.15f)
            {
                // Use the real finger direction, but keep the distance
                // configurable so it can be tuned without a rebuild.
                return local.normalized * Mathf.Clamp(
                    CoopPlugin.WeaponPalmOffset,
                    0.005f,
                    0.08f
                );
            }
        }

        // Unity humanoid hand bones point along local Y toward the fingers.
        return new Vector3(0.0f, CoopPlugin.WeaponPalmOffset, 0.0f);
    }

    private void ApplyWeaponScale(Transform weapon)
    {
        if (_weaponWorldScale.sqrMagnitude < 0.000001f)
        {
            weapon.localScale = _weaponLocalScale;
            return;
        }

        Transform? parent = weapon.parent;
        Vector3 parentScale = parent != null ? parent.lossyScale : Vector3.one;
        weapon.localScale = new Vector3(
            Mathf.Abs(parentScale.x) > 0.0001f ? _weaponWorldScale.x / parentScale.x : _weaponWorldScale.x,
            Mathf.Abs(parentScale.y) > 0.0001f ? _weaponWorldScale.y / parentScale.y : _weaponWorldScale.y,
            Mathf.Abs(parentScale.z) > 0.0001f ? _weaponWorldScale.z / parentScale.z : _weaponWorldScale.z
        );
    }

    private void ApplyWeaponTransform()
    {
        if (_weaponVisual == null)
        {
            return;
        }

        Transform weapon = _weaponVisual.transform;
        ApplyWeaponScale(weapon);

        if (!_weaponUseHandAnchor || _weaponHand == null || Root == null)
        {
            // No hand bone: keep the sender's parent-relative transform.
            weapon.localPosition = _weaponLocalPosition;
            weapon.localRotation = _weaponLocalRotation;
            return;
        }

        // The sender's real hand-to-grip offset is the best source. A zero
        // offset is valid here: the original IK puts the right hand bone
        // exactly on the weapon's GrabPoint. Only reject a helper that is far
        // away from the body hand.
        Vector3 handGrip = _weaponHandGripLocalPosition;
        float handGripLength = handGrip.magnitude;
        bool usedFallbackGrip = false;
        if (!_weaponHasHandGrip || handGripLength > 0.12f)
        {
            handGrip = GetFallbackHandGrip(_weaponHand);
            usedFallbackGrip = true;
        }

        Vector3 palmPosition = _weaponHand.TransformPoint(
            handGrip + GetWeaponGripNudge()
        );

        // Keep the weapon's real world orientation from the sender, so the
        // muzzle follows the camera aim even though the clone is third-person.
        Quaternion weaponRotation = GetRemoteWeaponRotation();
        Vector3 gripOffset = Vector3.zero;
        if (_weaponRightGrab != null)
        {
            // Align the real GrabPoint, not the weapon root, with the palm.
            gripOffset =
                weaponRotation *
                Vector3.Scale(_weaponGripLocalPosition, weapon.lossyScale);
        }

        weapon.rotation = weaponRotation;
        weapon.position = palmPosition - gripOffset;

        if (!_weaponPlacementLogged)
        {
            _weaponPlacementLogged = true;
            Vector3 gripWorld = _weaponRightGrab != null
                ? _weaponRightGrab.position
                : weapon.position;
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] id={Id} weapon placed '{_weaponItemName}' " +
                $"fallbackGrip={usedFallbackGrip} handGrip={handGrip} " +
                $"hand={_weaponHand.position} palm={palmPosition} " +
                $"grip={gripWorld} weapon={weapon.position} " +
                $"handToGrip={Vector3.Distance(_weaponHand.position, gripWorld):F3} " +
                $"handToPalm={Vector3.Distance(_weaponHand.position, palmPosition):F3} " +
                $"weaponRot={weapon.rotation.eulerAngles} " +
                $"handRot={_weaponHand.rotation.eulerAngles}."
            );
        }
    }

    private void DestroyWeaponVisual()
    {
        if (_weaponVisual != null)
        {
            UnityEngine.Object.Destroy(_weaponVisual);
        }

        _weaponVisual = null;
        _weaponMuzzle = null;
        _weaponFlash = null;
        _weaponAnimator = null;
        _weaponHand = null;
        _weaponRightGrab = null;
        _weaponLeftGrab = null;
        _weaponIk?.Clear();
    }

    private static void ClearSlotChildren(Transform parent)
    {
        for (int index = parent.childCount - 1; index >= 0; index--)
        {
            Transform child = parent.GetChild(index);
            if (child == null)
            {
                continue;
            }

            string name = child.name.ToLowerInvariant();
            if (name.Contains("hand") || name.Contains("ik") || name.Contains("joint"))
            {
                continue;
            }

            // WeaponLocation is a container used by the game's weapon
            // switching code. Keep it so the weapon's local transform has the
            // same parent path as on the sender, but remove the old visual
            // objects inside it.
            if (name.Contains("weaponlocation"))
            {
                ClearSlotChildren(child);
                continue;
            }

            if (child.GetComponentsInChildren<Renderer>(true).Length > 0)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }
    }

    private static Transform? FindTransformByPath(Transform root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        Transform current = root;
        string[] parts = path.Split('/');
        foreach (string part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            Transform? next = current.Find(part);
            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current == root ? null : current;
    }

    /// <summary>
    /// Finds the weapon's barrel axis in weapon-local space. The game's Muzzle
    /// helper is not always on the barrel: on the Makarov the reported offset is
    /// far outside the weapon bounds, and using it as the barrel made the pistol
    /// point up while the arms were horizontal. Prefer a muzzle that actually
    /// lies on the weapon, otherwise use the longest side of the render bounds
    /// pointing away from the grip.
    /// </summary>
    private static Vector3 FindBarrelLocalDirection(
        Transform weapon,
        Transform? muzzle,
        Vector3 gripLocal
    )
    {
        Bounds bounds = default;
        bool hasBounds = false;
        foreach (Renderer renderer in weapon.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !renderer.enabled)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        if (!hasBounds)
        {
            return Vector3.forward;
        }

        Vector3 centerLocal = weapon.InverseTransformPoint(bounds.center);
        Vector3 sizeLocal = weapon.InverseTransformVector(bounds.size);
        sizeLocal = new Vector3(
            Mathf.Abs(sizeLocal.x),
            Mathf.Abs(sizeLocal.y),
            Mathf.Abs(sizeLocal.z)
        );
        float longest = Mathf.Max(sizeLocal.x, Mathf.Max(sizeLocal.y, sizeLocal.z));
        if (longest < 0.0001f)
        {
            return Vector3.forward;
        }

        if (muzzle != null)
        {
            Vector3 muzzleLocal = weapon.InverseTransformPoint(muzzle.position);
            float muzzleDistance = Vector3.Distance(muzzleLocal, centerLocal);
            if (muzzleDistance <= longest * 1.25f)
            {
                Vector3 fromGrip = muzzleLocal - gripLocal;
                if (fromGrip.sqrMagnitude > 0.0001f)
                {
                    return fromGrip.normalized;
                }
            }
        }

        Vector3 axis = Vector3.forward;
        if (sizeLocal.y >= sizeLocal.x && sizeLocal.y >= sizeLocal.z)
        {
            axis = Vector3.up;
        }
        else if (sizeLocal.x >= sizeLocal.z)
        {
            axis = Vector3.right;
        }

        // The grip sits at the back of the weapon, so the barrel points from
        // the grip toward the center of the render bounds.
        if (Vector3.Dot(axis, centerLocal - gripLocal) < 0.0f)
        {
            axis = -axis;
        }

        return axis;
    }

    private static Transform? FindWeaponMuzzle(Transform weapon, string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            Transform? direct = weapon.Find(path);
            if (direct != null)
            {
                return direct;
            }
        }

        foreach (Transform child in weapon.GetComponentsInChildren<Transform>(true))
        {
            string name = child.name.ToLowerInvariant();
            if (name.Contains("muzzle") || name.Contains("muz") || name.Contains("barrelend"))
            {
                return child;
            }
        }

        return null;
    }

    private static Transform? FindGrabPoint(Transform weapon, string path, bool right)
    {
        if (!string.IsNullOrEmpty(path))
        {
            Transform? fromPath = FindTransformByPath(weapon, path);
            if (fromPath == null)
            {
                fromPath = weapon.Find(path);
            }

            if (fromPath != null)
            {
                return fromPath;
            }
        }

        // Fallback for weapons whose Shoot component was not available while
        // capturing the snapshot. The prefab names are stable in the game.
        string exact = right ? "rightgrab" : "leftgrab";
        string shortName = right ? "rgrab" : "lgrab";
        string side = right ? "r" : "l";
        Transform? best = null;
        int bestScore = 0;

        foreach (Transform child in weapon.GetComponentsInChildren<Transform>(true))
        {
            if (child == null)
            {
                continue;
            }

            string name = child.name.ToLowerInvariant();
            int score = 0;
            if (string.Equals(name, exact, StringComparison.Ordinal))
            {
                score = 100;
            }
            else if (string.Equals(name, shortName, StringComparison.Ordinal))
            {
                score = 90;
            }
            else if (name.Contains("grab"))
            {
                if (name.Contains(right ? "right" : "left"))
                {
                    score = 80;
                }
                else if (name.Contains($"grab_{side}") ||
                         name.Contains($"grab.{side}") ||
                         name.Contains($"grab-{side}") ||
                         name.Contains($"grab {side}"))
                {
                    score = 70;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = child;
            }
        }

        return best;
    }

    /// <summary>
    /// A two-bone approximation of the game's dragging IK. The game drags world
    /// objects with the left hand (IKControl.leftHandObj points at the physics
    /// interaction), so the left upper arm and forearm are aimed at a point in
    /// front of and below the character. Aiming in world space avoids the
    /// sideways arm that local-axis Euler offsets produced on the humanoid
    /// skeleton.
    /// </summary>
    private void ApplyDragHand(float weight)
    {
        if (!_bones.TryGetValue(BoneRole.LeftUpperArm, out BoneState upper) ||
            !_bones.TryGetValue(BoneRole.LeftLowerArm, out BoneState lower) ||
            upper.Bone == null ||
            lower.Bone == null ||
            Root == null)
        {
            return;
        }

        Vector3 shoulder = upper.Bone.position;
        float bob = Mathf.Sin(Time.unscaledTime * 7.0f) * 0.04f * weight;
        Vector3 target = Root.transform.position +
                         Root.transform.forward * (0.65f + bob) +
                         Vector3.up * 0.55f;

        Quaternion upperBefore = upper.Bone.rotation;
        Vector3 upperDirection = lower.Bone.position - shoulder;
        Vector3 targetDirection = target - shoulder;
        if (upperDirection.sqrMagnitude > 0.0001f && targetDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion upperDelta = Quaternion.FromToRotation(
                upperDirection.normalized,
                targetDirection.normalized
            );
            upper.Bone.rotation = Quaternion.Slerp(Quaternion.identity, upperDelta, weight) *
                                  upper.Bone.rotation;
        }

        if (CoopPlugin.ProbeEnabled && weight > 0.5f && Time.unscaledTime >= _nextDragLog)
        {
            _nextDragLog = Time.unscaledTime + 0.5f;
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] id={Id} drag pose bone='{upper.Bone.name}' " +
                $"before={upperBefore.eulerAngles} after={upper.Bone.rotation.eulerAngles} " +
                $"delta={Quaternion.Angle(upperBefore, upper.Bone.rotation):F1} " +
                $"shoulder={shoulder} target={target} weight={weight:F2}"
            );
        }

        // Re-evaluate after moving the upper arm so the forearm bends toward
        // the same target instead of keeping the old elbow direction.
        Vector3 elbow = lower.Bone.position;
        Vector3 elbowTarget = target - elbow;
        if (_bones.TryGetValue(BoneRole.LeftHand, out BoneState hand) && hand.Bone != null)
        {
            Vector3 forearmDirection = hand.Bone.position - elbow;
            if (forearmDirection.sqrMagnitude > 0.0001f && elbowTarget.sqrMagnitude > 0.0001f)
            {
                Quaternion lowerDelta = Quaternion.FromToRotation(
                    forearmDirection.normalized,
                    elbowTarget.normalized
                );
                lower.Bone.rotation = Quaternion.Slerp(Quaternion.identity, lowerDelta, weight) *
                                      lower.Bone.rotation;
            }
        }
    }

    /// <summary>
    /// Simple right-arm hold pose used only while the remote puppet is idle with
    /// a weapon. It is intentionally additive and is disabled while a weapon
    /// action clip is playing, so the original fire/reload/swing animation keeps
    /// control of the arm.
    /// </summary>
    private void ApplyWeaponHoldHand(float weight)
    {
        if (!_bones.TryGetValue(BoneRole.RightUpperArm, out BoneState upper) ||
            !_bones.TryGetValue(BoneRole.RightLowerArm, out BoneState lower) ||
            !_bones.TryGetValue(BoneRole.RightHand, out BoneState hand) ||
            upper.Bone == null ||
            lower.Bone == null ||
            hand.Bone == null ||
            Root == null)
        {
            return;
        }

        Vector3 shoulder = upper.Bone.position;
        Vector3 chest = _bones.TryGetValue(BoneRole.Chest, out BoneState chestBone) &&
                        chestBone.Bone != null
            ? chestBone.Bone.position
            : Root.transform.position + Vector3.up * 0.75f;

        // Follow the real camera aim, not the first-person weapon mesh. The
        // weapon mesh can sit at a "ready" angle that is far above the camera,
        // which made the hands rise and the pistol cover the face while the
        // sender was looking straight ahead.
        Vector3 aimDirection = _weaponCameraAim;
        if (aimDirection.sqrMagnitude < 0.0001f)
        {
            aimDirection = GetRemoteWeaponRotation() * _weaponBarrelLocalDirection;
        }

        // Use the camera only for the vertical aim. The forward part of the
        // hold must stay in front of the body even if the camera looks nearly
        // straight up or down. Otherwise the hands end up inside the chest.
        aimDirection.Normalize();
        float aimPitch = Mathf.Asin(Mathf.Clamp(aimDirection.y, -1.0f, 1.0f));
        float verticalAim = Mathf.Clamp(Mathf.Sin(aimPitch), -1.0f, 1.0f);
        Vector3 aimFlat = new Vector3(aimDirection.x, 0.0f, aimDirection.z);
        if (aimFlat.sqrMagnitude < 0.0001f)
        {
            aimFlat = Root.transform.forward;
        }
        aimFlat.Normalize();

        // Always keep the hands a little in front of the body. At horizontal
        // aim the target is straight ahead; looking all the way up or down
        // still leaves enough forward offset to keep the wrists and pistol out
        // of the chest instead of folding them into it. The original pose is
        // an extended two-hand grip at every pitch, so use one constant reach
        // rather than shortening the hold near vertical.
        const float forwardBias = 0.35f;
        const float holdDistance = 0.95f;
        Vector3 holdDirection = (
            aimFlat * forwardBias +
            Vector3.up * verticalAim
        ).normalized;
        if (holdDirection.sqrMagnitude < 0.0001f)
        {
            holdDirection = aimFlat;
        }

        Vector3 target = chest + holdDirection * holdDistance;
        _lastWeaponHoldTarget = target;
        _lastWeaponAimDirection = aimDirection;
        _lastWeaponAimPitch = aimPitch;

        float reach = (lower.Bone.position - upper.Bone.position).magnitude +
                      (hand.Bone.position - lower.Bone.position).magnitude;
        if (reach > 0.0001f)
        {
            target = shoulder + Vector3.ClampMagnitude(target - shoulder, reach * 0.98f);
        }

        Vector3 rightPole = shoulder +
                            Root.transform.right * 0.30f +
                            Root.transform.forward * 0.05f -
                            Vector3.up * 0.20f;
        SolveTwoBone(
            upper.Bone,
            lower.Bone,
            hand.Bone,
            target,
            rightPole,
            weight
        );

        if (!_weaponHoldLogged && weight > 0.9f)
        {
            _weaponHoldLogged = true;
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] id={Id} weapon hold target={target} chest={chest} " +
                $"shoulder={shoulder} aim={aimDirection} pitch={aimPitch:F2} " +
                $"handAfter={hand.Bone.position} " +
                $"distance={Vector3.Distance(hand.Bone.position, target):F3}."
            );
        }
    }

    /// <summary>
    /// Exact two-bone solve: the wrist lands on the target instead of merely
    /// pointing at it. The pole point chooses the elbow plane (down and out for
    /// the weapon hold), which keeps the arms from folding through the chest.
    /// </summary>
    private static void SolveTwoBone(
        Transform upper,
        Transform lower,
        Transform hand,
        Vector3 target,
        Vector3 polePoint,
        float weight
    )
    {
        Vector3 shoulder = upper.position;
        Vector3 elbow = lower.position;
        float upperLength = Vector3.Distance(shoulder, elbow);
        float lowerLength = Vector3.Distance(elbow, hand.position);
        if (upperLength < 0.0001f || lowerLength < 0.0001f)
        {
            return;
        }

        Vector3 toTarget = target - shoulder;
        float distance = toTarget.magnitude;
        if (distance < 0.0001f)
        {
            return;
        }

        distance = Mathf.Clamp(
            distance,
            Mathf.Abs(upperLength - lowerLength) + 0.001f,
            upperLength + lowerLength - 0.001f
        );
        Vector3 axis = toTarget.normalized;
        float along = (upperLength * upperLength -
                       lowerLength * lowerLength +
                       distance * distance) /
                      (2.0f * distance);
        float bendRadius = Mathf.Sqrt(
            Mathf.Max(upperLength * upperLength - along * along, 0.0f)
        );

        Vector3 poleDirection = polePoint - shoulder;
        Vector3 perpendicular = poleDirection -
                                axis * Vector3.Dot(poleDirection, axis);
        if (perpendicular.sqrMagnitude < 0.000001f)
        {
            perpendicular = (elbow - shoulder) -
                            axis * Vector3.Dot(elbow - shoulder, axis);
        }
        if (perpendicular.sqrMagnitude < 0.000001f)
        {
            perpendicular = Vector3.Cross(axis, Vector3.up);
        }
        if (perpendicular.sqrMagnitude < 0.000001f)
        {
            perpendicular = Vector3.Cross(axis, Vector3.forward);
        }

        Vector3 desiredElbow = shoulder +
                               axis * along +
                               perpendicular.normalized * bendRadius;
        Vector3 upperDirection = elbow - shoulder;
        Vector3 desiredUpperDirection = desiredElbow - shoulder;
        if (upperDirection.sqrMagnitude > 0.000001f &&
            desiredUpperDirection.sqrMagnitude > 0.000001f)
        {
            upper.rotation =
                Quaternion.Slerp(
                    Quaternion.identity,
                    Quaternion.FromToRotation(upperDirection, desiredUpperDirection),
                    weight
                ) * upper.rotation;
        }

        Vector3 newElbow = lower.position;
        Vector3 forearmDirection = hand.position - newElbow;
        Vector3 desiredForearmDirection = target - newElbow;
        if (forearmDirection.sqrMagnitude > 0.000001f &&
            desiredForearmDirection.sqrMagnitude > 0.000001f)
        {
            lower.rotation =
                Quaternion.Slerp(
                    Quaternion.identity,
                    Quaternion.FromToRotation(forearmDirection, desiredForearmDirection),
                    weight
                ) * lower.rotation;
        }
    }

    private void ApplyAdditive(BoneRole role, Quaternion offset)
    {
        if (_bones.TryGetValue(role, out BoneState bone) && bone.Bone != null)
        {
            bone.Bone.localRotation *= offset;
        }
    }

    private void ApplyGameAnimatorParameters(
        float vertical,
        float horizontal,
        float speed,
        float crouchTimer,
        bool crouch,
        bool sprint,
        bool inAir,
        bool jumping,
        bool shimmy
    )
    {
        if (_gameAnimator == null)
        {
            return;
        }

        SetFloatIfPresent(_gameAnimator, "Vertical", vertical);
        SetFloatIfPresent(_gameAnimator, "Horizontal", horizontal);
        SetFloatIfPresent(_gameAnimator, "MovementSpeed", speed);
        SetFloatIfPresent(_gameAnimator, "Speed", speed);
        SetFloatIfPresent(_gameAnimator, "CrouchTimer", crouchTimer);
        SetFloatIfPresent(_gameAnimator, "FPSwitch", 1.0f);
        SetBoolIfPresent(_gameAnimator, "Crouch", crouch);
        SetBoolIfPresent(_gameAnimator, "Sprint", sprint);
        SetBoolIfPresent(_gameAnimator, "InAir", inAir);
        SetBoolIfPresent(_gameAnimator, "Jumping", jumping);
        SetBoolIfPresent(_gameAnimator, "Shimmy", shimmy);
    }

    private static void SetFloatIfPresent(Animator animator, string name, float value)
    {
        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter.type == AnimatorControllerParameterType.Float &&
                string.Equals(parameter.name, name, StringComparison.OrdinalIgnoreCase))
            {
                animator.SetFloat(parameter.nameHash, value);
                return;
            }
        }
    }

    private static void SetBoolIfPresent(Animator animator, string name, bool value)
    {
        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter.type == AnimatorControllerParameterType.Bool &&
                string.Equals(parameter.name, name, StringComparison.OrdinalIgnoreCase))
            {
                animator.SetBool(parameter.nameHash, value);
                return;
            }
        }
    }

    /// <summary>
    /// Applies the remote player's alive/dead state. On death the Animator is
    /// stopped and the baked ragdoll rig is switched on so the body falls and
    /// stays down; on revive the rig goes inert again and the Animator resumes.
    /// </summary>
    public void SetDead(bool dead, Vector3 position, Quaternion rotation)
    {
        if (_dead == dead && !(dead && _pendingDeathPose))
        {
            return;
        }

        if (dead)
        {
            _dead = true;
            _pendingDeathPose = true;
            _pendingDeathPosition = position;
            _pendingDeathRotation = rotation;

            if (Root != null)
            {
                _pendingDeathPose = false;
                EnterRagdoll(position, rotation);
            }

            return;
        }

        _dead = false;
        _pendingDeathPose = false;
        if (Root != null)
        {
            ExitRagdoll();
        }
    }

    private void EnterRagdoll(Vector3 position, Quaternion rotation)
    {
        if (_ragdollActive)
        {
            return;
        }

        _ragdollActive = true;
        _ragdollFrozen = false;
        _ragdollAge = 0.0f;
        _position = position;
        _yaw = rotation.eulerAngles.y;
        Root.transform.position = position;
        Root.transform.rotation = rotation;

        if (_gameAnimator != null)
        {
            _gameAnimator.enabled = false;
        }

        // A dead body must not be lootable by the teammate.
        EnsureNotSearchable(Root);

        if (!CoopPlugin.PlayerDeathRagdoll)
        {
            // The rig is never switched on, so the body is already pinned.
            _ragdollFrozen = true;
            CoopPlugin.LogSource.LogInfo(
                $"[death] remote {Id} died; ragdoll disabled, body frozen."
            );
            return;
        }

        int bodies = 0;
        int colliders = 0;
        int joints = 0;
        try
        {
            foreach (Rigidbody body in _ragdollBodies)
            {
                if (body == null)
                {
                    continue;
                }

                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = false;
                body.useGravity = true;
                body.detectCollisions = true;
                bodies++;
            }

            int layer = ResolveRagdollLayer(position);
            foreach (Collider collider in _ragdollColliders)
            {
                if (collider != null)
                {
                    EnsureActive(collider.gameObject);
                    collider.enabled = true;
                    collider.isTrigger = false;
                    collider.gameObject.layer = layer;
                    colliders++;
                }
            }

            foreach (CharacterJoint joint in _ragdollJoints)
            {
                if (joint != null)
                {
                    joints++;
                }
            }

            CoopPlugin.LogSource.LogInfo(
                $"[death] ragdoll layer for remote {Id}: {layer}({LayerName(layer)}) " +
                $"(probe={LayerName(_ragdollProbeLayer)})."
            );
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] ragdoll failed for remote {Id}: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }

        CoopPlugin.LogSource.LogInfo(
            $"[death] remote {Id} is down (ragdoll bodies={bodies}, colliders={colliders}, " +
            $"joints={joints})."
        );
    }

    /// <summary>
    /// Picks a physics layer for the ragdoll colliders. The baked hitboxes live
    /// on a layer that only interacts with damage, so a body built from them
    /// falls through the level. The surface the body is lying on always collides
    /// with the world, so its layer is the safe choice; Default is the fallback.
    /// </summary>
    private int ResolveRagdollLayer(Vector3 position)
    {
        _ragdollProbeLayer = -1;
        try
        {
            // Identify the ragdoll's own colliders so the probe cannot report the
            // body itself as the floor.
            var own = new HashSet<int>();
            foreach (Collider collider in _ragdollColliders)
            {
                if (collider != null)
                {
                    own.Add(collider.GetInstanceID());
                }
            }

            // Ignore Raycast (the player's own layer) is excluded from the
            // default raycast mask, so this hits the surface under the body.
            RaycastHit[] hits = Physics.RaycastAll(
                position + Vector3.up * 0.4f,
                Vector3.down,
                8.0f
            );

            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || own.Contains(hit.collider.GetInstanceID()))
                {
                    continue;
                }

                int layer = hit.collider.gameObject.layer;
                _ragdollProbeLayer = layer;
                if (!Physics.GetIgnoreLayerCollision(layer, layer))
                {
                    return layer;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the default layer.
        }

        return 0;
    }

    private static string LayerName(int layer)
    {
        if (layer < 0 || layer > 31)
        {
            return "<none>";
        }

        string name = LayerMask.LayerToName(layer);
        return string.IsNullOrEmpty(name) ? "<unnamed>" : name;
    }

    private bool RagdollIsSettled()
    {
        foreach (Rigidbody body in _ragdollBodies)
        {
            if (body == null)
            {
                continue;
            }

            if (body.velocity.sqrMagnitude > 0.09f ||
                body.angularVelocity.sqrMagnitude > 0.25f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Pins a settled ragdoll in place: the limb bodies become kinematic so
    /// neither their own joints nor the other player's collider can push the
    /// corpse around any more.
    /// </summary>
    private void FreezeRagdollBodies()
    {
        _ragdollFrozen = true;
        int frozen = 0;

        try
        {
            foreach (Rigidbody body in _ragdollBodies)
            {
                if (body == null)
                {
                    continue;
                }

                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                body.useGravity = false;
                frozen++;
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] could not freeze the ragdoll for remote {Id}: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
            return;
        }

        CoopPlugin.LogSource.LogInfo(
            $"[death] remote {Id} corpse settled after {_ragdollAge:F2}s; " +
            $"froze {frozen} bodies."
        );
    }

    private void ExitRagdoll()
    {
        if (!_ragdollActive)
        {
            return;
        }

        _ragdollActive = false;
        _ragdollFrozen = false;
        _ragdollAge = 0.0f;

        try
        {
            foreach (Rigidbody body in _ragdollBodies)
            {
                if (body == null)
                {
                    continue;
                }

                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                body.useGravity = false;
                body.detectCollisions = false;
            }

            for (int i = 0; i < _ragdollColliders.Count; i++)
            {
                Collider? collider = _ragdollColliders[i];
                if (collider != null)
                {
                    collider.enabled = false;
                    if (i < _ragdollOriginalLayers.Count)
                    {
                        collider.gameObject.layer = _ragdollOriginalLayers[i];
                    }
                }
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] ragdoll reset failed for remote {Id}: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }

        if (_gameAnimator != null)
        {
            _gameAnimator.enabled = true;
        }

        // Snap to the sender's current transform: the ragdoll moved the body
        // away from it, and the puppet must reappear where the revived player
        // actually is instead of walking back from the death spot.
        _position = TargetPosition;
        _yaw = TargetYaw;
        Root.transform.position = _position;
        Root.transform.rotation = Quaternion.Euler(0.0f, _yaw, 0.0f);
        CoopPlugin.LogSource.LogInfo($"[death] remote {Id} is back up.");
    }

    /// <summary>
    /// Makes sure a ragdoll collider's GameObject is live in the hierarchy,
    /// walking up to (but not past) the avatar root. Some baked hitboxes live
    /// under objects the game disables outside of a real ragdoll.
    /// </summary>
    private void EnsureActive(GameObject go)
    {
        try
        {
            if (go.activeInHierarchy)
            {
                return;
            }

            go.SetActive(true);
            Transform? parent = go.transform.parent;
            while (parent != null &&
                   parent != Root.transform &&
                   !parent.gameObject.activeInHierarchy)
            {
                parent.gameObject.SetActive(true);
                parent = parent.parent;
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }

    /// <summary>
    /// Finds the baked ragdoll rig on the freshly cloned model and makes it
    /// inert. Must run before the hitbox scripts and colliders are stripped.
    /// </summary>
    private void CollectRagdollRig(GameObject clone)
    {
        _ragdollBodies.Clear();
        _ragdollColliders.Clear();
        _ragdollJoints.Clear();
        _ragdollOriginalLayers.Clear();
        _ragdollInfo.Clear();

        // The hitbox colliders sit on a layer that only interacts with damage
        // rays, so a ragdoll built from them would fall straight through the
        // level. The player's own layer does collide with the world, so the
        // ragdoll is moved onto it while the body is down.
        _ragdollLayer = clone.layer;

        try
        {
            void Collect(GameObject go)
            {
                Rigidbody? body = go.GetComponent<Rigidbody>();
                if (body != null && !_ragdollBodies.Contains(body))
                {
                    _ragdollBodies.Add(body);
                }

                foreach (Collider collider in go.GetComponents<Collider>())
                {
                    if (collider != null && !_ragdollColliders.Contains(collider))
                    {
                        _ragdollColliders.Add(collider);
                        _ragdollOriginalLayers.Add(collider.gameObject.layer);
                        _ragdollInfo.Add(
                            $"{go.name}/{collider.GetType().Name}:layer={collider.gameObject.layer}," +
                            $"trigger={collider.isTrigger},enabled={collider.enabled}," +
                            $"active={collider.gameObject.activeInHierarchy}"
                        );
                    }
                }

                foreach (CharacterJoint joint in go.GetComponents<CharacterJoint>())
                {
                    if (joint != null && !_ragdollJoints.Contains(joint))
                    {
                        _ragdollJoints.Add(joint);
                    }
                }
            }

            // The hitboxes name the ragdoll parts; the joints catch any holder
            // that has no hitbox (for example the hip root).
            foreach (HitBox hitBox in clone.GetComponentsInChildren<HitBox>(true))
            {
                if (hitBox != null)
                {
                    Collect(hitBox.gameObject);
                }
            }

            foreach (CharacterJoint joint in clone.GetComponentsInChildren<CharacterJoint>(true))
            {
                if (joint != null)
                {
                    Collect(joint.gameObject);
                }
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] ragdoll rig scan failed: {exception.GetType().Name}: {exception.Message}"
            );
        }

        foreach (Rigidbody body in _ragdollBodies)
        {
            body.isKinematic = true;
            body.useGravity = false;
            body.detectCollisions = false;
        }

        foreach (Collider collider in _ragdollColliders)
        {
            collider.enabled = false;
        }

        CoopPlugin.LogSource.LogInfo(
            $"[death] ragdoll rig for remote {Id}: bodies={_ragdollBodies.Count}, " +
            $"colliders={_ragdollColliders.Count}, joints={_ragdollJoints.Count}, " +
            $"colliderLayers=[{DescribeLayers(_ragdollOriginalLayers)}], " +
            $"playerLayer={_ragdollLayer}({LayerName(_ragdollLayer)}), " +
            $"hitboxLayers={DescribeHitboxLayers(clone)}."
        );
        CoopPlugin.LogSource.LogInfo(
            $"[death] ragdoll colliders for remote {Id}: {string.Join(" | ", _ragdollInfo)}"
        );
        CoopPlugin.LogSource.LogInfo($"[death] layers: {DescribeAllLayers()}");
    }

    private static string DescribeLayers(List<int> layers)
    {
        var names = new List<string>(layers.Count);
        foreach (int layer in layers)
        {
            names.Add($"{layer}({LayerName(layer)})");
        }

        return string.Join(",", names);
    }

    private static string DescribeAllLayers()
    {
        var parts = new List<string>(32);
        for (int layer = 0; layer < 32; layer++)
        {
            string name = LayerMask.LayerToName(layer);
            if (!string.IsNullOrEmpty(name))
            {
                parts.Add($"{layer}={name}");
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Reports the layers the game's own ragdoll switches between, for
    /// diagnostics: the baked hitboxes only collide with the world once the
    /// game's HitBoxes.Ragroll moves them off these layers.
    /// </summary>
    private static string DescribeHitboxLayers(GameObject clone)
    {
        try
        {
            HitBoxes? hitBoxes = clone.GetComponent<HitBoxes>();
            if (hitBoxes == null)
            {
                return "<none>";
            }

            return $"starting={hitBoxes.startingLayer.value}," +
                $"default={hitBoxes.defaultLayer.value}," +
                $"gore={hitBoxes.goreLayer.value}";
        }
        catch (Exception exception)
        {
            return $"<{exception.GetType().Name}>";
        }
    }

    /// <summary>
    /// Guarantees the remote body can never be searched or looted. Looting is
    /// driven by the game's <c>Interaction</c>/<c>container</c> components; the
    /// clone is already stripped of every MonoBehaviour, and this removes any
    /// that a runtime system might have added to the puppet.
    /// </summary>
    private static void EnsureNotSearchable(GameObject root)
    {
        int removed = 0;
        try
        {
            removed += DestroyAll<Interaction>(root);
            removed += DestroyAll<container>(root);
            removed += DestroyAll<frob>(root);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] loot guard failed: {exception.GetType().Name}: {exception.Message}"
            );
            return;
        }

        if (removed > 0)
        {
            CoopPlugin.LogSource.LogInfo(
                $"[death] removed {removed} loot/interaction component(s) from the remote body."
            );
        }
    }

    private static int DestroyAll<T>(GameObject root) where T : Component
    {
        int count = 0;
        foreach (T component in root.GetComponentsInChildren<T>(true))
        {
            if (component != null)
            {
                UnityEngine.Object.DestroyImmediate(component, true);
                count++;
            }
        }

        return count;
    }

    public void Update(float deltaTime)
    {
        if (Root == null)
        {
            return;
        }

        // A dead body belongs to the ragdoll. Writing the smoothed pose here
        // every frame overwrote the ragdoll's rotation with a yaw-only one and
        // dragged the corpse along with the sender's transform, which is what
        // made a teammate's body "crawl away".
        if (_dead)
        {
            if (_pendingDeathPose)
            {
                _pendingDeathPose = false;
                EnterRagdoll(_pendingDeathPosition, _pendingDeathRotation);
            }

            // Pin the body once the fall has settled so it cannot keep sliding.
            if (!_ragdollFrozen && _ragdollActive)
            {
                _ragdollAge += deltaTime;
                if (_ragdollAge >= RagdollMinSettle &&
                    (_ragdollAge >= RagdollMaxSettle || RagdollIsSettled()))
                {
                    FreezeRagdollBodies();
                }
            }

            return;
        }

        float t = 1.0f - Mathf.Exp(-12.0f * deltaTime);
        _position = Vector3.Lerp(_position, TargetPosition, t);
        _yaw = Mathf.LerpAngle(_yaw, TargetYaw, t);

        Root.transform.position = _position;
        Root.transform.rotation = Quaternion.Euler(0.0f, _yaw, 0.0f);

        // Some pause implementations disable animators or restore their update
        // mode. Keep the remote puppet independent from the local pause menu.
        if (_gameAnimator != null && Time.timeScale <= 0.001f)
        {
            if (!_gameAnimator.enabled)
            {
                _gameAnimator.enabled = true;
            }

            if (_gameAnimator.updateMode != AnimatorUpdateMode.UnscaledTime)
            {
                _gameAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
            }
        }

        ApplyProceduralAnimation(deltaTime);
    }

    public void Destroy()
    {
        _hitProxy = null;
        _ragdollBodies.Clear();
        _ragdollColliders.Clear();
        _ragdollJoints.Clear();
        _ragdollOriginalLayers.Clear();
        _ragdollInfo.Clear();
        if (Root != null)
        {
            UnityEngine.Object.Destroy(Root);
        }
    }

    private GameObject CreateVisual(Transform? source, out bool cloned)
    {
        cloned = false;
        source ??= UnityEngine.Object.FindObjectOfType<PlayerMovement>()?.transform;

        if (source != null && !IsMenuScene(source.gameObject.scene.name))
        {
            try
            {
                GameObject clone = ClonePlayerModel(source);
                if (clone != null)
                {
                    cloned = true;
                    return clone;
                }
            }
            catch (Exception exception)
            {
                CoopPlugin.LogSource.LogWarning(
                    $"[avatar] failed to clone player model: {exception.GetType().Name}: {exception.Message}"
                );
            }
        }

        GameObject capsule = CreateCapsule();
        _hitProxy = CombatSync.AttachHitProxy(capsule, source, null, Id);
        return capsule;
    }

    private GameObject ClonePlayerModel(Transform source)
    {
        GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, source.position, source.rotation);
        clone.name = $"PeripeteiaCoop_Remote_{Id}";
        clone.SetActive(false);

        if (CoopPlugin.ProbeEnabled)
        {
            LogSourceAnimators(clone);
        }

        // First-person arms and effects must not be visible on a remote clone.
        DestroyChild(clone.transform, "Mesh");
        DestroyChild(clone.transform, "DeathTheme");
        DestroyChild(clone.transform, "CoatActivator");
        DestroyChild(clone.transform, "Voice");
        DestroyChild(clone.transform, "SurfSphere");

        // Remove every gameplay component, keeping transforms, mesh data and the
        // Animator. Start with all animators disabled; CacheSkeleton later
        // enables exactly one for full body animation (or keeps it disabled for
        // the procedural fallback).
        foreach (Animator animator in clone.GetComponentsInChildren<Animator>(true))
        {
            if (animator != null)
            {
                animator.enabled = false;
                animator.applyRootMotion = false;
            }
        }

        // The player model carries a baked hitbox ragdoll. Keep it (inert) so a
        // remote death can collapse the body. This must run before the hitbox
        // scripts and colliders are stripped below.
        CollectRagdollRig(clone);

        // Remove gameplay scripts with typed lookups. Do not iterate
        // GetComponentsInChildren<Component>(): Il2CppInterop returns base
        // Component wrappers there, so type tests fail and the old loop was
        // deleting MeshFilter components. That left the renderers alive but
        // without meshes, which is why only a few body pieces were visible.
        foreach (MonoBehaviour behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour != null)
            {
                UnityEngine.Object.DestroyImmediate(behaviour, true);
            }
        }

        // Never let the puppet offer a search/loot prompt, dead or alive.
        EnsureNotSearchable(clone);

        foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
        {
            if (collider != null && !_ragdollColliders.Contains(collider))
            {
                UnityEngine.Object.DestroyImmediate(collider, true);
            }
        }

        foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
        {
            if (body != null && !_ragdollBodies.Contains(body))
            {
                UnityEngine.Object.DestroyImmediate(body, true);
            }
        }

        foreach (AudioSource audioSource in clone.GetComponentsInChildren<AudioSource>(true))
        {
            if (audioSource != null)
            {
                UnityEngine.Object.DestroyImmediate(audioSource, true);
            }
        }

        foreach (AudioListener audioListener in clone.GetComponentsInChildren<AudioListener>(true))
        {
            if (audioListener != null)
            {
                UnityEngine.Object.DestroyImmediate(audioListener, true);
            }
        }

        foreach (Camera camera in clone.GetComponentsInChildren<Camera>(true))
        {
            if (camera != null)
            {
                UnityEngine.Object.DestroyImmediate(camera, true);
            }
        }

        foreach (Light light in clone.GetComponentsInChildren<Light>(true))
        {
            if (light != null)
            {
                UnityEngine.Object.DestroyImmediate(light, true);
            }
        }

        // The game's body pieces use custom Renderer subclasses, so preserving
        // only MeshRenderer/SkinnedMeshRenderer used to delete most of Marie and
        // leave just the coat and skirt. Preserve active states from the source:
        // inactive clothing variants must stay hidden, while the two visual roots
        // are explicitly enabled for the remote third-person model.
        foreach (string visualRootName in new[] { "UpperMesh", "Skelly" })
        {
            Transform? visualRoot = clone.transform.Find(visualRootName);
            if (visualRoot != null)
            {
                if (CoopPlugin.ProbeEnabled)
                {
                    int rootRenderers = visualRoot.GetComponentsInChildren<Renderer>(true).Length;
                    CoopPlugin.LogSource.LogInfo(
                        $"[avatar] visual root '{visualRootName}' activeSelf={visualRoot.gameObject.activeSelf} " +
                        $"activeInHierarchy={visualRoot.gameObject.activeInHierarchy} renderers={rootRenderers}"
                    );
                }

                visualRoot.gameObject.SetActive(true);
            }
        }

        if (CoopPlugin.ProbeEnabled)
        {
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                {
                    continue;
                }

                string path = BuildPath(renderer.transform, clone.transform);
                if (path.IndexOf("Cube", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("coat", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    CoopPlugin.LogSource.LogInfo(
                        $"[avatar] source renderer '{path}' enabled={renderer.enabled} " +
                        $"activeSelf={renderer.gameObject.activeSelf}"
                    );
                }
            }
        }

        foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !renderer.gameObject.activeSelf)
            {
                continue;
            }

            // The Marie model contains a second, identical pair of arm meshes
            // named "Cube.001 (1)" / "Cube.003 (1)". The game hides one pair at
            // runtime through scripts that the clone does not have, so a remote
            // body otherwise renders four arms. Keep the base pair and hide the
            // duplicate. Also preserve renderer.enabled for the rest: some
            // clothing/augment pieces (for example "upper left arm.002") are
            // intentionally disabled by the source.
            string objectName = renderer.gameObject.name;
            if (string.Equals(objectName, "Cube.001 (1)", StringComparison.Ordinal) ||
                string.Equals(objectName, "Cube.003 (1)", StringComparison.Ordinal))
            {
                renderer.enabled = false;
            }
        }

        clone.SetActive(true);

        CacheSkeleton(clone);

        // The body clone is made from the local player, so it carries the local
        // player's currently equipped weapon. Remove it; WeaponSync attaches a
        // visual of the remote player's actual weapon.
        Transform? clonedWeaponSlot = clone.transform.Find("UpperPivot/WeaponPivot/WeaponSlot");
        if (clonedWeaponSlot != null)
        {
            ClearSlotChildren(clonedWeaponSlot);
        }

        LogRendererSummary(clone);
        if (CoopPlugin.ProbeEnabled)
        {
            DumpCloneHands(clone);
        }

        _hitProxy = CombatSync.AttachHitProxy(clone, source, _gameAnimator, Id);

        return clone;
    }

    private static void DumpCloneHands(GameObject clone)
    {
        foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
        {
            string path = BuildPath(renderer.transform, clone.transform);
            if (!renderer.gameObject.activeInHierarchy || !renderer.enabled)
            {
                continue;
            }

            string material = "-";
            try
            {
                if (renderer.sharedMaterial != null)
                {
                    material = renderer.sharedMaterial.name;
                }
            }
            catch
            {
                // Renderer wrapper may not expose materials on every custom type.
            }

            Bounds bounds = renderer.bounds;
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] visible renderer '{path}' type={renderer.GetType().Name} " +
                $"material='{material}' size=({bounds.size.x:F2},{bounds.size.y:F2},{bounds.size.z:F2})"
            );

            try
            {
                SkinnedMeshRenderer? skinned = renderer.TryCast<SkinnedMeshRenderer>();
                if (skinned != null && skinned.rootBone != null)
                {
                    string firstBone = skinned.bones.Length > 0 && skinned.bones[0] != null
                        ? skinned.bones[0].name
                        : "-";
                    string firstBonePath = skinned.bones.Length > 0 && skinned.bones[0] != null
                        ? BuildPath(skinned.bones[0], clone.transform)
                        : "-";
                    string armPath = "-";
                    foreach (Transform bone in skinned.bones)
                    {
                        if (bone != null && bone.name.IndexOf("ArmUpper.L", StringComparison.Ordinal) >= 0)
                        {
                            armPath = BuildPath(bone, clone.transform);
                            break;
                        }
                    }

                    CoopPlugin.LogSource.LogInfo(
                        $"[avatar] visible skinned '{path}' " +
                        $"rootBone='{BuildPath(skinned.rootBone, clone.transform)}' " +
                        $"firstBone='{firstBonePath}' armBone='{armPath}' bones={skinned.bones.Length}"
                    );
                }
            }
            catch (Exception exception)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[avatar] skinned cast failed for '{path}': {exception.GetType().Name}"
                );
            }
        }

        foreach (Transform transform in clone.GetComponentsInChildren<Transform>(true))
        {
            string name = transform.name;
            if (name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                CoopPlugin.LogSource.LogInfo(
                    $"[avatar] limb '{BuildPath(transform, clone.transform)}' " +
                    $"activeSelf={transform.gameObject.activeSelf} " +
                    $"activeInHierarchy={transform.gameObject.activeInHierarchy} " +
                    $"renderers={transform.GetComponentsInChildren<Renderer>(true).Length}"
                );
            }
        }
    }

    private static string BuildPath(Transform transform, Transform root)
    {
        var parts = new List<string>();
        Transform? current = transform;
        while (current != null)
        {
            parts.Add(current.name);
            if (current == root)
            {
                break;
            }

            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private void CacheSkeleton(GameObject clone)
    {
        _bones.Clear();
        _gameAnimator = null;

        Animator? skeleton = null;
        foreach (Animator animator in clone.GetComponentsInChildren<Animator>(true))
        {
            if (animator != null && animator.avatar != null)
            {
                skeleton = animator;
                break;
            }
        }

        if (skeleton == null)
        {
            CoopPlugin.LogSource.LogWarning("[avatar] no humanoid Animator for procedural animation.");
            return;
        }

        foreach (Animator animator in clone.GetComponentsInChildren<Animator>(true))
        {
            if (animator == null)
            {
                continue;
            }

            if (CoopPlugin.Settings.GameAnimator && animator == skeleton)
            {
                animator.enabled = true;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                // The remote player must keep animating when this client pauses
                // its own game (Time.timeScale = 0). All mod ticking already uses
                // unscaled time; the clone Animator has to match.
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                _gameAnimator = animator;
                if (CoopPlugin.ProbeEnabled)
                {
                    LogAnimatorParameters(animator);
                }

                CoopPlugin.LogSource.LogInfo(
                    $"[avatar] full animation Animator enabled on '{animator.name}'."
                );
            }
            else
            {
                animator.enabled = false;
            }
        }

        AddBone(skeleton, HumanBodyBones.Hips, BoneRole.Hips);
        AddBone(skeleton, HumanBodyBones.Spine, BoneRole.Spine);
        AddBone(skeleton, HumanBodyBones.Chest, BoneRole.Chest);
        AddBone(skeleton, HumanBodyBones.LeftUpperLeg, BoneRole.LeftUpperLeg);
        AddBone(skeleton, HumanBodyBones.RightUpperLeg, BoneRole.RightUpperLeg);
        AddBone(skeleton, HumanBodyBones.LeftLowerLeg, BoneRole.LeftLowerLeg);
        AddBone(skeleton, HumanBodyBones.RightLowerLeg, BoneRole.RightLowerLeg);
        AddBone(skeleton, HumanBodyBones.LeftUpperArm, BoneRole.LeftUpperArm);
        AddBone(skeleton, HumanBodyBones.RightUpperArm, BoneRole.RightUpperArm);
        AddBone(skeleton, HumanBodyBones.LeftLowerArm, BoneRole.LeftLowerArm);
        AddBone(skeleton, HumanBodyBones.RightLowerArm, BoneRole.RightLowerArm);
        AddBone(skeleton, HumanBodyBones.LeftHand, BoneRole.LeftHand);
        AddBone(skeleton, HumanBodyBones.RightHand, BoneRole.RightHand);

        if (_gameAnimator == null)
        {
            SetNaturalArmPose(skeleton);
        }

        CoopPlugin.LogSource.LogInfo(
            $"[avatar] procedural bones={_bones.Count} (game Animator={(_gameAnimator != null ? "on" : "off")})."
        );
    }

    private static void LogAnimatorParameters(Animator animator)
    {
        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] animator parameter '{parameter.name}' type={parameter.type}"
            );
        }
    }

    /// <summary>
    /// The game may keep the hidden third-person body in a T-pose while the
    /// local player is in first-person. Rotate the upper arms down once so the
    /// remote puppet starts in a natural A-pose; the procedural cycle then adds
    /// swing on top of that base.
    /// </summary>
    private void SetNaturalArmPose(Animator animator)
    {
        Transform? chest = animator.GetBoneTransform(HumanBodyBones.Chest)
            ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        if (chest == null)
        {
            return;
        }

        AdjustArm(animator, chest, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, -1.0f, BoneRole.LeftUpperArm);
        AdjustArm(animator, chest, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 1.0f, BoneRole.RightUpperArm);
    }

    private void AdjustArm(
        Animator animator,
        Transform chest,
        HumanBodyBones upperBone,
        HumanBodyBones lowerBone,
        float fallbackSide,
        BoneRole role
    )
    {
        Transform? upper = animator.GetBoneTransform(upperBone);
        Transform? lower = animator.GetBoneTransform(lowerBone);
        if (upper == null || lower == null)
        {
            return;
        }

        Vector3 current = lower.position - upper.position;
        if (current.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Vector3 direction = current.normalized;
        if (Mathf.Abs(direction.y) > 0.7f)
        {
            return;
        }

        float side = Mathf.Sign(upper.position.x - chest.position.x);
        if (Mathf.Approximately(side, 0.0f))
        {
            side = fallbackSide;
        }

        Vector3 target = new Vector3(side * 0.3f, -1.0f, 0.05f).normalized;
        Quaternion delta = Quaternion.FromToRotation(direction, target);
        upper.rotation = delta * upper.rotation;
        _bones[role] = new BoneState(upper, upper.localRotation);

        if (CoopPlugin.ProbeEnabled)
        {
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] arm pose {upper.name}: {direction.x:F2},{direction.y:F2},{direction.z:F2} -> " +
                $"{target.x:F2},{target.y:F2},{target.z:F2}"
            );
        }
    }

    private void AddBone(Animator animator, HumanBodyBones humanBone, BoneRole role)
    {
        Transform? bone = animator.GetBoneTransform(humanBone);
        if (bone != null)
        {
            _bones[role] = new BoneState(bone, bone.localRotation);
        }
    }

    private void ApplyProceduralAnimation(float deltaTime)
    {
        if (_dead || _gameAnimator != null || _bones.Count == 0)
        {
            return;
        }

        float movement = Mathf.Clamp01(Mathf.Abs(_vertical) + Mathf.Abs(_horizontal));
        if (movement < 0.05f)
        {
            movement = Mathf.Clamp01(Mathf.Abs(_speed));
        }

        bool airborne = _inAir || _jumping;
        float crouch = _crouch ? 1.0f : 0.0f;

        _phase += deltaTime * Mathf.Lerp(1.5f, 9.0f, movement);

        float swing = Mathf.Sin(_phase) * 34.0f * movement;
        float kneeLeft = Mathf.Max(0.0f, -Mathf.Sin(_phase)) * 42.0f * movement;
        float kneeRight = Mathf.Max(0.0f, Mathf.Sin(_phase)) * 42.0f * movement;
        float armSwing = swing * 0.7f;

        float spineTilt = crouch * 12.0f + movement * 3.0f;
        float spineBob = Mathf.Sin(_phase * 2.0f) * 1.5f * movement;

        Quaternion leftLeg = Quaternion.Euler(swing + crouch * 28.0f, 0.0f, 0.0f);
        Quaternion rightLeg = Quaternion.Euler(-swing + crouch * 28.0f, 0.0f, 0.0f);
        Quaternion leftKnee = Quaternion.Euler(-kneeLeft - crouch * 45.0f, 0.0f, 0.0f);
        Quaternion rightKnee = Quaternion.Euler(-kneeRight - crouch * 45.0f, 0.0f, 0.0f);
        Quaternion leftArm = Quaternion.Euler(-armSwing, 0.0f, 0.0f);
        Quaternion rightArm = Quaternion.Euler(armSwing, 0.0f, 0.0f);
        Quaternion leftElbow = Quaternion.Euler(-Mathf.Abs(armSwing) * 0.5f, 0.0f, 0.0f);
        Quaternion rightElbow = Quaternion.Euler(-Mathf.Abs(armSwing) * 0.5f, 0.0f, 0.0f);
        Quaternion spine = Quaternion.Euler(spineTilt + spineBob, 0.0f, 0.0f);

        if (airborne)
        {
            leftLeg = Quaternion.Euler(-18.0f, 0.0f, 0.0f);
            rightLeg = Quaternion.Euler(10.0f, 0.0f, 0.0f);
            leftKnee = Quaternion.Euler(-30.0f, 0.0f, 0.0f);
            rightKnee = Quaternion.Euler(-18.0f, 0.0f, 0.0f);
            leftArm = Quaternion.Euler(18.0f, 0.0f, 0.0f);
            rightArm = Quaternion.Euler(18.0f, 0.0f, 0.0f);
        }

        float smoothing = 1.0f - Mathf.Exp(-14.0f * deltaTime);

        ApplyBone(BoneRole.LeftUpperLeg, leftLeg, smoothing);
        ApplyBone(BoneRole.RightUpperLeg, rightLeg, smoothing);
        ApplyBone(BoneRole.LeftLowerLeg, leftKnee, smoothing);
        ApplyBone(BoneRole.RightLowerLeg, rightKnee, smoothing);
        ApplyBone(BoneRole.LeftUpperArm, leftArm, smoothing);
        ApplyBone(BoneRole.RightUpperArm, rightArm, smoothing);
        ApplyBone(BoneRole.LeftLowerArm, leftElbow, smoothing);
        ApplyBone(BoneRole.RightLowerArm, rightElbow, smoothing);
        ApplyBone(BoneRole.Spine, spine, smoothing);
    }

    private void ApplyBone(BoneRole role, Quaternion offset, float smoothing)
    {
        if (!_bones.TryGetValue(role, out BoneState bone) || bone.Bone == null)
        {
            return;
        }

        Quaternion target = bone.BaseRotation * offset;
        bone.Bone.localRotation = Quaternion.Slerp(bone.Bone.localRotation, target, smoothing);
    }

    private static void LogSourceAnimators(GameObject clone)
    {
        var animators = clone.GetComponentsInChildren<Animator>(true);
        CoopPlugin.LogSource.LogInfo($"[avatar] source animators={animators.Length} (kept disabled for bone lookup)");

        for (int i = 0; i < animators.Length; i++)
        {
            Animator animator = animators[i];
            if (animator == null)
            {
                continue;
            }

            string controller = animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.name
                : "<null>";
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] source animator '{animator.name}' controller='{controller}' " +
                $"enabled={animator.enabled} human={animator.avatar != null && animator.avatar.isHuman}"
            );
        }
    }

    private static void DestroyChild(Transform parent, string name)
    {
        Transform child = parent.Find(name);
        if (child != null)
        {
            UnityEngine.Object.DestroyImmediate(child.gameObject);
        }
    }

    private static void LogRendererSummary(GameObject clone)
    {
        var renderers = clone.GetComponentsInChildren<Renderer>(true);
        int enabled = 0;

        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null && renderers[i].enabled)
            {
                enabled++;
            }
        }

        CoopPlugin.LogSource.LogInfo(
            $"[avatar] cloned player model: renderers={renderers.Length}, enabled={enabled}, children={clone.transform.childCount}"
        );
    }

    private GameObject CreateCapsule()
    {
        GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        capsule.name = $"PeripeteiaCoop_Remote_{Id}";
        capsule.transform.localScale = new Vector3(0.7f, 0.95f, 0.7f);

        Collider collider = capsule.GetComponent<Collider>();
        if (collider != null)
        {
            UnityEngine.Object.Destroy(collider);
        }

        Renderer renderer = capsule.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material material = renderer.material;
            material.color = Id == 0
                ? new Color(0.15f, 0.85f, 1.0f, 0.9f)
                : new Color(1.0f, 0.25f, 0.75f, 0.9f);
        }

        _usingFallback = true;
        CoopPlugin.LogSource.LogInfo("[avatar] falling back to capsule visual.");
        return capsule;
    }
}

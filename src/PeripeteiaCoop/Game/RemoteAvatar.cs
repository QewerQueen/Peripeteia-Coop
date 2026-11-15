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
    private Transform? _weaponRightGrab;
    private Transform? _weaponLeftGrab;
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

    private int _actionHash;
    private float _actionTime;
    private bool _actionActive;
    private int _appliedActionHash;
    private float _appliedActionTime;

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
        Root.transform.position = position;
        Root.transform.rotation = Quaternion.Euler(0.0f, yaw, 0.0f);

        UnityEngine.Object.DontDestroyOnLoad(Root);
    }

    public int Id { get; }

    public string Name { get; set; }

    public GameObject Root { get; private set; }

    public Vector3 TargetPosition { get; set; }

    public float TargetYaw { get; set; }

    public bool HasPose { get; private set; }

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

            // The old weapon visual was a child of the old root and has just
            // been destroyed with it. Recreate it from the last state packet.
            _weaponVisual = null;
            _weaponMuzzle = null;
            _weaponFlash = null;
            _weaponAnimator = null;
            _weaponRightGrab = null;
            _weaponLeftGrab = null;
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
        if (Root == null || !Root.activeInHierarchy)
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

        visual.transform.SetParent(parent, false);
        _weaponVisual = visual;
        // The sender sends the weapon's real local transform relative to the
        // same skeleton bone. Applying it verbatim copies the game's own
        // attachment, including the grip position inside the palm. The config
        // offset is only a final nudge and defaults to zero.
        _weaponVisual.transform.localPosition =
            _weaponLocalPosition + GetWeaponGripNudge();
        _weaponVisual.transform.localRotation = _weaponLocalRotation;
        _weaponVisual.transform.localScale = _weaponLocalScale;

        _weaponRightGrab = FindGrabPoint(visual.transform, _weaponRightGrabPath, true);
        _weaponLeftGrab = FindGrabPoint(visual.transform, _weaponLeftGrabPath, false);

        EnsureWeaponIk();

        _weaponMuzzle = FindWeaponMuzzle(visual.transform, _weaponMuzzlePath);
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

        CoopPlugin.LogSource.LogInfo(
            $"[avatar] id={Id} weapon visual '{_weaponItemName}' slot='{parent.name}' " +
            $"parentPath='{_weaponParentPath}' " +
            $"anchor={(useHandAnchor ? "hand" : "path")} " +
            $"rightGrab={(_weaponRightGrab != null ? _weaponRightGrab.name : "<none>")} " +
            $"leftGrab={(_weaponLeftGrab != null ? _weaponLeftGrab.name : "<none>")} " +
            $"oneHanded={_weaponOneHanded} " +
            $"localPos={_weaponLocalPosition} localRot={_weaponLocalRotation.eulerAngles} " +
            $"gripDown={CoopPlugin.WeaponGripDown:F3} " +
            $"gripForward={CoopPlugin.WeaponGripForward:F3} " +
            $"weaponWorld={_weaponVisual.transform.position} " +
            $"handWorld={(hand != null ? hand.position : Vector3.zero)} " +
            $"muzzle={(_weaponMuzzle != null ? _weaponMuzzle.name : "<none>")} " +
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
        // The exact attachment comes from the sender. These two values are only
        // a final manual nudge in the hand's local space and default to zero.
        return new Vector3(
            0.0f,
            -CoopPlugin.WeaponGripDown,
            CoopPlugin.WeaponGripForward
        );
    }

    private void ApplyWeaponTransform()
    {
        if (_weaponVisual == null)
        {
            return;
        }

        Transform weapon = _weaponVisual.transform;
        weapon.localPosition = _weaponLocalPosition + GetWeaponGripNudge();
        weapon.localRotation = _weaponLocalRotation;
        weapon.localScale = _weaponLocalScale;
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
        Vector3 target = chest +
                         Root.transform.forward * 0.45f +
                         Root.transform.right * 0.10f;

        float reach = (lower.Bone.position - upper.Bone.position).magnitude +
                      (hand.Bone.position - lower.Bone.position).magnitude;
        if (reach > 0.0001f)
        {
            target = shoulder + Vector3.ClampMagnitude(target - shoulder, reach * 0.92f);
        }

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

        Vector3 elbow = lower.Bone.position;
        Vector3 forearmDirection = hand.Bone.position - elbow;
        Vector3 elbowTarget = target - elbow;
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

    public void Update(float deltaTime)
    {
        if (Root == null)
        {
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

        return CreateCapsule();
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

        foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
        {
            if (collider != null)
            {
                UnityEngine.Object.DestroyImmediate(collider, true);
            }
        }

        foreach (Rigidbody body in clone.GetComponentsInChildren<Rigidbody>(true))
        {
            if (body != null)
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
        if (_gameAnimator != null || _bones.Count == 0)
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

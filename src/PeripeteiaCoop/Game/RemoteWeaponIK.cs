using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Minimal replacement for the game's IKControl on a remote body. The original
/// IKControl belongs to the gameplay player and is stripped from the clone, so
/// the support hand is solved here instead. Unity calls OnAnimatorIK after the
/// Animator has written the locomotion/action pose and before LateUpdate, which
/// makes this the same place where the game solves its own hand IK.
/// </summary>
public sealed class RemoteWeaponIK : MonoBehaviour
{
    private Animator? _animator;
    private Transform? _leftGrab;
    private Transform? _leftUpperArm;
    private Transform? _leftLowerArm;
    private Transform? _leftHand;
    private Vector3 _handRotationMod;
    private float _weight;
    private bool _targetActive;
    private bool _loggedCallback;
    private bool _loggedActive;
    private bool _loggedTarget;

    public void Configure(Animator animator, Vector3 handRotationMod)
    {
        _animator = animator;
        _handRotationMod = handRotationMod;
        CacheBones();
    }

    private void CacheBones()
    {
        if (_animator == null || !_animator.isHuman)
        {
            return;
        }

        _leftUpperArm ??= _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        _leftLowerArm ??= _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        _leftHand ??= _animator.GetBoneTransform(HumanBodyBones.LeftHand);
    }

    public void SetHandRotationMod(Vector3 handRotationMod)
    {
        _handRotationMod = handRotationMod;
    }

    public void SetLeftGrab(Transform? leftGrab, bool active)
    {
        _leftGrab = leftGrab;
        _targetActive = active && leftGrab != null;
    }

    public void Clear()
    {
        _leftGrab = null;
        _targetActive = false;
    }

    private void BendSupportArm(Vector3 target, float weight)
    {
        if (_leftUpperArm == null || _leftLowerArm == null || _leftHand == null)
        {
            return;
        }

        // Point the support arm at the weapon before Unity's IK solve. If the
        // target is reachable the IK solver refines the hand; if it is not, the
        // arm still looks like it is trying to hold the weapon instead of
        // hanging at the side.
        Vector3 shoulder = _leftUpperArm.position;
        Vector3 upperDirection = _leftLowerArm.position - shoulder;
        Vector3 targetDirection = target - shoulder;
        if (upperDirection.sqrMagnitude > 0.0001f &&
            targetDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion upperDelta = Quaternion.FromToRotation(
                upperDirection.normalized,
                targetDirection.normalized
            );
            _leftUpperArm.rotation =
                Quaternion.Slerp(Quaternion.identity, upperDelta, weight) *
                _leftUpperArm.rotation;
        }

        Vector3 elbow = _leftLowerArm.position;
        Vector3 forearmDirection = _leftHand.position - elbow;
        Vector3 elbowTarget = target - elbow;
        if (forearmDirection.sqrMagnitude > 0.0001f &&
            elbowTarget.sqrMagnitude > 0.0001f)
        {
            Quaternion lowerDelta = Quaternion.FromToRotation(
                forearmDirection.normalized,
                elbowTarget.normalized
            );
            _leftLowerArm.rotation =
                Quaternion.Slerp(Quaternion.identity, lowerDelta, weight) *
                _leftLowerArm.rotation;
        }
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (!_loggedCallback)
        {
            _loggedCallback = true;
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] weapon IK callback active on '{name}' " +
                $"(human={( _animator != null && _animator.isHuman ? "yes" : "no")})."
            );
        }

        if (_animator == null || !_animator.isHuman)
        {
            return;
        }

        float target = _targetActive && _leftGrab != null ? 1.0f : 0.0f;
        float deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        _weight = Mathf.MoveTowards(_weight, target, deltaTime * 10.0f);

        if (_weight <= 0.001f)
        {
            _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0.0f);
            _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0.0f);
            return;
        }

        if (!_loggedActive)
        {
            _loggedActive = true;
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] weapon IK holding support hand on " +
                $"'{( _leftGrab != null ? _leftGrab.name : "<none>")}'."
            );
        }

        Transform grab = _leftGrab!;
        CacheBones();
        BendSupportArm(grab.position, _weight);

        if (!_loggedTarget && _weight > 0.9f)
        {
            _loggedTarget = true;
            Vector3 handPosition = _leftHand != null
                ? _leftHand.position
                : Vector3.zero;
            CoopPlugin.LogSource.LogInfo(
                $"[avatar] weapon IK target='{grab.name}' " +
                $"leftHand={handPosition} grab={grab.position} " +
                $"distance={Vector3.Distance(handPosition, grab.position):F3} " +
                $"weight={_weight:F2}."
            );
        }

        _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, _weight);
        _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, _weight);
        _animator.SetIKPosition(AvatarIKGoal.LeftHand, grab.position);
        _animator.SetIKRotation(
            AvatarIKGoal.LeftHand,
            grab.rotation * Quaternion.Euler(_handRotationMod)
        );
    }
}

using System;
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
    private bool _loggedError;

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

    public float CurrentWeight => _weight;

    private void BendSupportArm(Vector3 target, float weight)
    {
        if (_leftUpperArm == null || _leftLowerArm == null || _leftHand == null)
        {
            return;
        }

        Vector3 shoulder = _leftUpperArm.position;
        Vector3 polePoint = shoulder -
                            _animator!.transform.right * 0.25f +
                            _animator.transform.forward * 0.05f -
                            Vector3.up * 0.20f;
        SolveTwoBone(
            _leftUpperArm,
            _leftLowerArm,
            _leftHand,
            target,
            polePoint,
            weight
        );
    }

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

    private void OnAnimatorIK(int layerIndex)
    {
        // Unity calls this every frame; a single null bone or a stale IL2CPP
        // wrapper used to throw here and spam the log with NullReferenceException.
        try
        {
            OnAnimatorIKImpl(layerIndex);
        }
        catch (Exception exception)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                CoopPlugin.LogSource.LogWarning(
                    $"[avatar] weapon IK disabled after error on '{name}': " +
                    $"{exception.GetType().Name}: {exception.Message}"
                );
            }

            _targetActive = false;
            _weight = 0.0f;
        }
    }

    private void OnAnimatorIKImpl(int layerIndex)
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

        if (_leftUpperArm != null)
        {
            // Keep the support elbow out and down. Without a hint Unity can fold
            // the elbow through the chest, which reads as the left arm hanging
            // down even when the hand reaches the weapon.
            Vector3 hint =
                _leftUpperArm.position -
                _animator.transform.right * 0.25f +
                _animator.transform.forward * 0.05f -
                Vector3.up * 0.20f;
            _animator.SetIKHintPosition(AvatarIKHint.LeftElbow, hint);
            _animator.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, _weight);
        }

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

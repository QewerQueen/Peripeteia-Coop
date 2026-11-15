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
    private Vector3 _handRotationMod;
    private float _weight;
    private bool _targetActive;
    private bool _loggedCallback;
    private bool _loggedActive;

    public void Configure(Animator animator, Vector3 handRotationMod)
    {
        _animator = animator;
        _handRotationMod = handRotationMod;
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
        _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, _weight);
        _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, _weight);
        _animator.SetIKPosition(AvatarIKGoal.LeftHand, grab.position);
        _animator.SetIKRotation(
            AvatarIKGoal.LeftHand,
            grab.rotation * Quaternion.Euler(_handRotationMod)
        );
    }
}

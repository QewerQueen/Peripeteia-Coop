using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Physics proxy that turns a visual-only remote puppet into something the local
/// game can actually hit. The remote model is a clone with every gameplay
/// script, collider and rigidbody stripped, so without this component bullets
/// pass straight through the other player and you can walk through them.
///
/// The proxy owns two colliders: a capsule for the body and a small sphere for
/// the head. They sit on the same physics layer as the local player (see
/// <see cref="CombatSync.ResolveProxyLayer"/>), so the local CharacterController
/// is blocked by the other player and the mod's own shot raycast can tell body
/// and head hits apart.
///
/// This type is injected into IL2CPP at runtime, so it must stay a plain
/// MonoBehaviour with a parameterless constructor.
/// </summary>
public sealed class RemoteHitProxy : MonoBehaviour
{
    private int _bodyColliderId;
    private int _headColliderId;
    private Transform? _headBone;
    private Transform? _headCollider;
    private Vector3 _headOffset;

    /// <summary>Co-op id of the player this proxy stands for (host = 0).</summary>
    public int AvatarId { get; private set; }

    /// <summary>Height of the body capsule at build time, used by debug logging.</summary>
    public float Height { get; private set; }

    public void Configure(int avatarId, Collider? body, Collider? head, float height)
    {
        AvatarId = avatarId;
        Height = height;
        _bodyColliderId = body != null ? body.GetInstanceID() : 0;
        _headColliderId = head != null ? head.GetInstanceID() : 0;
        CombatSync.RegisterProxy(this, body, head);
    }

    /// <summary>
    /// Makes the head collider follow the animated head bone. The sphere is a
    /// child of the proxy (so it is part of the same kinematic rigidbody) and is
    /// repositioned every frame instead of being parented to the skeleton, which
    /// avoids putting a rigidbody on an animated bone.
    /// </summary>
    public void BindHead(Transform? headBone, Vector3 headOffset)
    {
        _headBone = headBone;
        _headOffset = headOffset;
        _headCollider = transform.Find("PeripeteiaCoop_HitProxy_Head");
    }

    public bool IsHead(int colliderId)
    {
        return _headColliderId != 0 && colliderId == _headColliderId;
    }

    public bool OwnsCollider(int colliderId)
    {
        return colliderId == _bodyColliderId || colliderId == _headColliderId;
    }

    private void LateUpdate()
    {
        if (_headBone == null || _headCollider == null)
        {
            return;
        }

        if (_headBone.gameObject == null || !_headBone.gameObject.activeInHierarchy)
        {
            return;
        }

        _headCollider.position = _headBone.position + _headOffset;
    }

    private void OnDestroy()
    {
        CombatSync.UnregisterProxy(this);
    }
}

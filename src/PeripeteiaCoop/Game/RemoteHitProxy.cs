using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Physics proxy that turns a visual-only remote puppet into something the local
/// game can actually hit. The remote model is a clone with every gameplay
/// script, collider and rigidbody stripped, so without this component bullets
/// pass straight through the other player and you can walk through them.
///
/// The proxy owns a capsule for the torso, a small sphere for the head and a
/// set of capsules that follow the arm and leg bones. The limb capsules are what
/// makes the whole body hittable: the torso capsule alone misses an extended
/// arm, so shots at a raised weapon or a reaching hand used to fly straight
/// through the teammate.
///
/// Everything sits on the same physics layer as the local player (see
/// <see cref="CombatSync.ResolveProxyLayer"/>), so the local CharacterController
/// is blocked by the other player and the mod's own shot raycast can tell body,
/// head and limb hits apart.
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

    // Limb capsules. A fixed array keeps the injected type free of generic
    // collections of custom types; there are at most eight limb segments.
    private const int MaxLimbs = 8;
    private readonly Transform?[] _limbA = new Transform?[MaxLimbs];
    private readonly Transform?[] _limbB = new Transform?[MaxLimbs];
    private readonly CapsuleCollider?[] _limbCapsule = new CapsuleCollider?[MaxLimbs];
    private readonly float[] _limbRadius = new float[MaxLimbs];
    private int _limbCount;

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

    /// <summary>
    /// Adds a capsule collider that spans one limb segment between two bones.
    /// <paramref name="radius"/> is the limb thickness in world units. The
    /// capsule is repositioned to match the animated bones every frame.
    /// </summary>
    public void BindLimb(Transform? a, Transform? b, float radius, int layer)
    {
        if (a == null || b == null || _limbCount >= MaxLimbs)
        {
            return;
        }

        var limbObject = new GameObject("PeripeteiaCoop_HitProxy_Limb");
        limbObject.transform.SetParent(transform, false);
        limbObject.layer = layer;

        var capsule = limbObject.AddComponent<CapsuleCollider>();
        capsule.direction = 1;
        capsule.radius = radius;
        capsule.height = Mathf.Max(radius * 2.0f, Vector3.Distance(a.position, b.position) + radius);
        capsule.isTrigger = false;

        CombatSync.RegisterProxyCollider(this, capsule);

        _limbA[_limbCount] = a;
        _limbB[_limbCount] = b;
        _limbCapsule[_limbCount] = capsule;
        _limbRadius[_limbCount] = radius;
        _limbCount++;
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
        UpdateHead();
        UpdateLimbs();
    }

    private void UpdateHead()
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

    private void UpdateLimbs()
    {
        for (int i = 0; i < _limbCount; i++)
        {
            Transform? a = _limbA[i];
            Transform? b = _limbB[i];
            CapsuleCollider? capsule = _limbCapsule[i];
            if (a == null || b == null || capsule == null)
            {
                continue;
            }

            if (a.gameObject == null || b.gameObject == null ||
                !a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy)
            {
                capsule.enabled = false;
                continue;
            }

            Vector3 delta = b.position - a.position;
            float length = delta.magnitude;
            if (length < 0.01f)
            {
                capsule.enabled = false;
                continue;
            }

            capsule.enabled = true;
            Transform limbTransform = capsule.transform;
            limbTransform.position = a.position + delta * 0.5f;
            limbTransform.rotation = Quaternion.FromToRotation(Vector3.up, delta / length);

            float radius = _limbRadius[i];
            capsule.radius = radius;
            capsule.height = Mathf.Max(radius * 2.0f, length + radius);
        }
    }

    private void OnDestroy()
    {
        CombatSync.UnregisterProxy(this);
    }
}

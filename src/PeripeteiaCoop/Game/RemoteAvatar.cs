using System;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Remote player visual. We clone the local player's model hierarchy and strip
/// every gameplay component from the clone: it must never read input, run AI,
/// play audio or collide with the world. Only transforms, mesh renderers and
/// mesh filters survive, so the clone is a static puppet driven by network
/// packets. Animation data is already recorded in <see cref="SetAnimation"/>
/// but is not applied yet (see docs/REVERSE_ENGINEERING.md).
/// </summary>
internal sealed class RemoteAvatar
{
    private Vector3 _position;
    private float _yaw;
    private bool _usingFallback;

    public RemoteAvatar(int id, string name, Vector3 position, float yaw, Transform? source)
    {
        Id = id;
        Name = name;
        TargetPosition = position;
        TargetYaw = yaw;
        _position = position;
        _yaw = yaw;

        Root = CreateVisual(source);
        Root.transform.position = position;
        Root.transform.rotation = Quaternion.Euler(0.0f, yaw, 0.0f);

        UnityEngine.Object.DontDestroyOnLoad(Root);
    }

    public int Id { get; }

    public string Name { get; set; }

    public GameObject Root { get; private set; }

    public Vector3 TargetPosition { get; set; }

    public float TargetYaw { get; set; }

    public void SetTarget(Vector3 position, float yaw)
    {
        TargetPosition = position;
        TargetYaw = yaw;
    }

    /// <summary>
    /// Replaces the capsule placeholder when the local player model becomes
    /// available after the connection was already established (for example
    /// while a level is loading).
    /// </summary>
    public void TryUpgrade(Transform? source)
    {
        if (!_usingFallback || source == null || Root == null)
        {
            return;
        }

        try
        {
            GameObject upgraded = ClonePlayerModel(source);
            upgraded.transform.position = Root.transform.position;
            upgraded.transform.rotation = Root.transform.rotation;
            UnityEngine.Object.DontDestroyOnLoad(upgraded);

            UnityEngine.Object.Destroy(Root);
            Root = upgraded;
            _usingFallback = false;
            CoopPlugin.LogSource.LogInfo($"[avatar] upgraded avatar id={Id} from capsule to player model.");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[avatar] failed to upgrade avatar id={Id}: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Records the same animator parameters that PlayerMovement drives locally.
    /// The clone currently has no Animator (keeping it froze the main thread in
    /// testing), so these values are reserved for the animation milestone.
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
        // TODO(M2b): drive a safe Animator (or procedural bones) with these
        // values. Keeping the game's Animator on a stripped clone stalled the
        // Unity main thread in testing, so the clone is static for now.
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
    }

    public void Destroy()
    {
        if (Root != null)
        {
            UnityEngine.Object.Destroy(Root);
        }
    }

    private GameObject CreateVisual(Transform? source)
    {
        source ??= UnityEngine.Object.FindObjectOfType<PlayerMovement>()?.transform;

        if (source != null)
        {
            try
            {
                GameObject clone = ClonePlayerModel(source);
                if (clone != null)
                {
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

        LogSourceAnimators(clone);

        // First-person arms and effects must not be visible on a remote clone.
        DestroyChild(clone.transform, "Mesh");
        DestroyChild(clone.transform, "DeathTheme");
        DestroyChild(clone.transform, "CoatActivator");
        DestroyChild(clone.transform, "Voice");
        DestroyChild(clone.transform, "SurfSphere");

        // Remove every gameplay component, keeping only transforms and mesh
        // data. The Animator is stripped as well; see SetAnimation().
        foreach (Component component in clone.GetComponentsInChildren<Component>(true))
        {
            if (component == null ||
                component is Transform ||
                component is MeshRenderer ||
                component is SkinnedMeshRenderer ||
                component is MeshFilter)
            {
                continue;
            }

            UnityEngine.Object.DestroyImmediate(component, true);
        }

        // The clone must be visible even if it was created while the source was
        // in first-person mode. Do not call playerSkins.setFirstThirdPerson on
        // the clone: that method mutates shared camera state and makes the local
        // CameraPivot throw NullReferenceException every frame.
        foreach (Transform child in clone.GetComponentsInChildren<Transform>(true))
        {
            child.gameObject.SetActive(true);
        }

        foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = true;
        }

        clone.SetActive(true);
        LogRendererSummary(clone);

        return clone;
    }

    private static void LogSourceAnimators(GameObject clone)
    {
        var animators = clone.GetComponentsInChildren<Animator>(true);
        CoopPlugin.LogSource.LogInfo($"[avatar] source animators={animators.Length} (stripped from clone)");

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
            CoopPlugin.LogSource.LogInfo($"[avatar] source animator '{animator.name}' controller='{controller}'");
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

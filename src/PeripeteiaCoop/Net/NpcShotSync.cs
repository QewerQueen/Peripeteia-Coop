using System;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Replicates NPC gunfire to the client so that a fight the host is running is
/// visible on the other machine.
///
/// The host simulates every NPC, so an enemy's muzzle flash and tracer only
/// exist on the host. The client mirrors the NPC's transform and animation but
/// its AI is frozen (<c>Ai.FreezeClientAi</c>), so the mirrored weapon never
/// fires and the client sees an enemy shooting with no visible shot. This module
/// forwards each NPC shot as a muzzle origin and a direction; the client traces
/// the ray against its own copy of the level and draws a short-lived tracer (and
/// a muzzle flash) so incoming fire is readable.
///
/// Damage is deliberately not part of this: NPC damage already reaches the
/// client through <see cref="CombatSync"/>. The tracer is purely cosmetic, which
/// is why a lost packet is harmless and the whole thing runs on the unreliable
/// channel.
/// </summary>
internal static class NpcShotSync
{
    // Per-weapon cap so a burst weapon cannot flood the link.
    private const float MinSendInterval = 0.03f;

    // Longest tracer we will draw when the ray hits nothing.
    private const float MaxRange = 160.0f;

    // Shots farther than this from the client's body are not worth a packet.
    private const float ClientReach = 110.0f;

    private const int LogLimit = 8;

    private static Material? _tracerMaterial;
    private static float _nextSendTime;
    private static int _sent;
    private static int _received;
    private static int _logged;

    public static int Sent => _sent;

    public static int Received => _received;

    /// <summary>Host: called from the Shoot.Fire patches for every weapon.</summary>
    public static void NotifyShot(Shoot shoot)
    {
        if (CoopPlugin.Settings.Role != CoopRole.Host ||
            !CoopPlugin.AiEnabled ||
            !CoopPlugin.AiMirrorEnemies ||
            !CoopPlugin.AiFreezeClientAi ||
            shoot == null ||
            CoopPlugin.Network == null ||
            CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return;
        }

        // The player's own weapon already flashes a muzzle through
        // WeaponEvent.Shot; only NPC weapons are handled here.
        if (WeaponSync.IsLocalPlayerWeapon(shoot))
        {
            return;
        }

        if (Time.unscaledTime < _nextSendTime)
        {
            return;
        }

        if (!CombatSync.TryResolveShotRay(shoot, out Vector3 origin, out Vector3 direction, out _))
        {
            return;
        }

        if (!HasClientNearby(origin))
        {
            return;
        }

        _nextSendTime = Time.unscaledTime + MinSendInterval;
        _sent++;
        CoopPlugin.Network.SendNpcShot(origin, direction);
    }

    private static bool HasClientNearby(Vector3 origin)
    {
        float reach = ClientReach * ClientReach;
        foreach (int id in CoopPlugin.Network!.RemotePlayerIds())
        {
            Transform? root = CoopPlugin.Network.GetAvatarRoot(id);
            if (root != null && (root.position - origin).sqrMagnitude <= reach)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Client: draws the tracer for a shot the host reported.</summary>
    public static void SpawnVisual(Vector3 origin, Vector3 direction)
    {
        _received++;

        if (!CoopPlugin.AiEnabled ||
            !CoopPlugin.AiMirrorEnemies ||
            !CoopPlugin.AiFreezeClientAi ||
            direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        direction.Normalize();

        Vector3 end = origin + direction * MaxRange;
        try
        {
            if (Physics.Raycast(
                    origin,
                    direction,
                    out RaycastHit hit,
                    MaxRange,
                    ~0,
                    QueryTriggerInteraction.Ignore
                ) &&
                hit.distance > 0.05f)
            {
                end = hit.point;
            }
        }
        catch (Exception)
        {
            // Fall back to the full-length tracer.
        }

        DrawTracer(origin, end);

        if (_logged < LogLimit)
        {
            _logged++;
            CoopPlugin.LogSource.LogInfo(
                $"[tracer] npc shot {origin} -> {end} ({_logged}/{LogLimit})"
            );
        }
    }

    private static void DrawTracer(Vector3 from, Vector3 to)
    {
        try
        {
            var go = new GameObject("PeripeteiaCoop_NpcTracer");
            var line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.startWidth = 0.045f;
            line.endWidth = 0.02f;
            line.useWorldSpace = true;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.numCapVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.startColor = new Color(1.0f, 0.93f, 0.62f, 0.9f);
            line.endColor = new Color(1.0f, 0.62f, 0.22f, 0.15f);

            Material? material = TracerMaterial();
            if (material != null)
            {
                line.material = material;
            }

            UnityEngine.Object.Destroy(go, 0.07f);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[tracer] could not draw npc tracer: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
            return;
        }

        SpawnMuzzleFlash(from);
    }

    private static void SpawnMuzzleFlash(Vector3 position)
    {
        try
        {
            var go = new GameObject("PeripeteiaCoop_NpcMuzzleFlash");
            go.transform.position = position;
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 6.0f;
            light.intensity = 2.2f;
            light.color = new Color(1.0f, 0.85f, 0.55f);
            UnityEngine.Object.Destroy(go, 0.05f);
        }
        catch (Exception)
        {
            // Cosmetic only; never let a light break the shot.
        }
    }

    /// <summary>One shared material so tracers do not leak materials.</summary>
    private static Material? TracerMaterial()
    {
        if (_tracerMaterial != null)
        {
            return _tracerMaterial;
        }

        try
        {
            Shader? shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            if (shader != null)
            {
                _tracerMaterial = new Material(shader);
            }
        }
        catch (Exception)
        {
            _tracerMaterial = null;
        }

        return _tracerMaterial;
    }
}

[HarmonyPatch(typeof(Shoot), "Fire", new Type[] { typeof(LayerMask), typeof(int) })]
internal static class NpcShotFireSimplePatch
{
    private static void Postfix(Shoot __instance)
    {
        NpcShotSync.NotifyShot(__instance);
    }
}

[HarmonyPatch(typeof(Shoot), "Fire", new Type[] { typeof(Vector3), typeof(LayerMask), typeof(int) })]
internal static class NpcShotFireAtPointPatch
{
    private static void Postfix(Shoot __instance)
    {
        NpcShotSync.NotifyShot(__instance);
    }
}

[HarmonyPatch(
    typeof(Shoot),
    "Fire",
    new Type[] { typeof(Vector3), typeof(LayerMask), typeof(int), typeof(bool) }
)]
internal static class NpcShotFireAtPointTriggerPatch
{
    private static void Postfix(Shoot __instance)
    {
        NpcShotSync.NotifyShot(__instance);
    }
}

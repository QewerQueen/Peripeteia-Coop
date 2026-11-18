using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Applies a dragged corpse pose to the local player's own dead body.
///
/// While a teammate carries your body they stream the rig they are moving (see
/// <see cref="RemoteAvatar.SendCorpsePose"/>). The carrier owns a puppet of you,
/// so their side looks right, but on your own machine nothing moved the real
/// ragdoll and the body stayed where it fell. This module finds the same ragdoll
/// parts on the local body (in the same order the puppet uses, so the streamed
/// indices line up) and pins them to the carried pose.
///
/// The bodies are left non-kinematic so a revive can take over again; only the
/// transform and velocities are forced each update.
/// </summary>
internal static class LocalCorpse
{
    private const int LogLimit = 3;

    private static readonly List<Rigidbody> _bodies = new();
    private static PlayerMovement? _player;
    private static int _applied;
    private static int _logged;

    public static int Applied => _applied;

    /// <summary>
    /// Pins the local body to a pose streamed by the player carrying it. Called
    /// with the pose of the local player's own corpse (id == local id).
    /// </summary>
    public static void Apply(
        PlayerMovement? player,
        Vector3[] positions,
        Quaternion[] rotations
    )
    {
        if (player == null ||
            positions == null ||
            rotations == null ||
            positions.Length == 0)
        {
            return;
        }

        if (!EnsureBodies(player))
        {
            if (_logged < LogLimit)
            {
                _logged++;
                CoopPlugin.LogSource.LogInfo(
                    "[death] carried corpse pose received but the local body has no " +
                    "ragdoll parts to move."
                );
            }

            return;
        }

        int count = Mathf.Min(
            positions.Length,
            Mathf.Min(rotations.Length, _bodies.Count)
        );

        try
        {
            for (int i = 0; i < count; i++)
            {
                Rigidbody? body = _bodies[i];
                if (body == null)
                {
                    continue;
                }

                body.position = positions[i];
                body.rotation = rotations[i];
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[death] could not apply the carried pose to the local body: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
            _bodies.Clear();
            return;
        }

        _applied++;
        if (_logged < LogLimit)
        {
            _logged++;
            CoopPlugin.LogSource.LogInfo(
                $"[death] applied the carried corpse pose to the local body " +
                $"({count}/{_bodies.Count} parts)."
            );
        }
    }

    /// <summary>
    /// Collects the local body's ragdoll parts in the same order
    /// <see cref="RemoteAvatar.CollectRagdollRig"/> uses for the puppet (hitboxes
    /// first, then any remaining joints), so the streamed part indices match.
    /// </summary>
    private static bool EnsureBodies(PlayerMovement player)
    {
        if (_player == player && _bodies.Count > 0 && _bodies[0] != null)
        {
            return true;
        }

        _player = player;
        _bodies.Clear();

        try
        {
            void Collect(GameObject go)
            {
                Rigidbody? body = go.GetComponent<Rigidbody>();
                if (body != null && !_bodies.Contains(body))
                {
                    _bodies.Add(body);
                }
            }

            foreach (HitBox hitBox in player.GetComponentsInChildren<HitBox>(true))
            {
                if (hitBox != null)
                {
                    Collect(hitBox.gameObject);
                }
            }

            foreach (CharacterJoint joint in player.GetComponentsInChildren<CharacterJoint>(true))
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
                $"[death] local ragdoll scan failed: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
        }

        return _bodies.Count > 0;
    }

    /// <summary>Forgets the cached rig, e.g. after a scene change or a revive.</summary>
    public static void Reset()
    {
        _bodies.Clear();
        _player = null;
    }
}

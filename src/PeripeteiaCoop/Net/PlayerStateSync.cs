using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Shares player death and revival between the two machines.
///
/// Health, damage and inventory stay personal per player, but a death is
/// something the whole party must see: the remote puppet goes down as a ragdoll
/// where it fell and stands back up on revive. This module only watches the
/// local player's real <see cref="HealthScript.dead"/> flag and broadcasts the
/// transition; the actual ragdoll is driven by
/// <see cref="RemoteAvatar.SetDead(bool, Vector3, Quaternion)"/> on the other
/// machine, so no remote health is ever mutated from here.
///
/// The body is deliberately not lootable: the remote puppet is a stripped clone
/// with no <c>Interaction</c>/<c>container</c> components, so the game never
/// offers a search prompt on a dead teammate (see
/// <c>RemoteAvatar.EnsureNotSearchable</c>).
/// </summary>
internal static class PlayerStateSync
{
    private const float ResendInterval = 5.0f;

    private static bool _initialized;
    private static bool _lastDead;
    private static float _nextResendTime;
    private static int _deathsSent;
    private static int _deathsApplied;

    /// <summary>
    /// Deaths that arrived before the remote puppet existed. The sender keeps
    /// resending while dead, but a player can die within the first second of a
    /// level, so the state is remembered and applied as soon as the puppet
    /// spawns instead of being dropped.
    /// </summary>
    private static readonly Dictionary<int, PendingDeath> Pending = new();

    private struct PendingDeath
    {
        public bool Dead;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    public static bool Enabled =>
        CoopPlugin.Settings.Role != CoopRole.Offline && CoopPlugin.PlayerSyncDeath;

    public static int DeathsSent => _deathsSent;

    public static int DeathsApplied => _deathsApplied;

    public static void Tick(PlayerMovement? localPlayer)
    {
        if (!Enabled)
        {
            return;
        }

        if (localPlayer == null || localPlayer.gameObject == null)
        {
            // Do not drop the baseline here. A respawn destroys the local player
            // and creates a fresh one a moment later; forgetting the last state
            // would make that new body the "first sample", which swallows the
            // dead -> alive transition and leaves the teammate's corpse on the
            // ground forever.
            return;
        }

        HealthScript? health = localPlayer.GetComponent<HealthScript>();
        if (health == null)
        {
            health = localPlayer.GetComponentInParent<HealthScript>();
        }

        if (health == null)
        {
            return;
        }

        bool dead = health.dead;

        if (!_initialized)
        {
            // The first sample only seeds the baseline: both instances load the
            // same save, so an already-dead state is not a transition.
            _initialized = true;
            _lastDead = dead;
            _nextResendTime = Time.unscaledTime + ResendInterval;
            return;
        }

        bool changed = dead != _lastDead;
        bool resend = dead && Time.unscaledTime >= _nextResendTime;
        if (!changed && !resend)
        {
            return;
        }

        _lastDead = dead;
        _nextResendTime = Time.unscaledTime + ResendInterval;
        _deathsSent++;

        CoopPlugin.Network?.SendPlayerDeath(
            CoopPlugin.Network.LocalId,
            dead,
            localPlayer.transform.position,
            localPlayer.transform.rotation
        );

        CoopPlugin.LogSource.LogInfo(
            $"[death] local player {(dead ? "died" : "revived")} at " +
            $"{localPlayer.transform.position}."
        );
    }

    public static void Apply(int id, bool dead, Vector3 position, Quaternion rotation)
    {
        RemoteAvatar? avatar = CoopPlugin.Network?.GetAvatar(id);
        if (avatar == null)
        {
            // The puppet is not spawned yet; remember it and try again in Tick.
            Pending[id] = new PendingDeath
            {
                Dead = dead,
                Position = position,
                Rotation = rotation,
            };
            return;
        }

        Pending.Remove(id);

        if (avatar.IsDead == dead)
        {
            return;
        }

        _deathsApplied++;
        avatar.SetDead(dead, position, rotation);
    }

    /// <summary>Flushes deaths that arrived before their puppet existed.</summary>
    public static void FlushPending()
    {
        if (Pending.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<int, PendingDeath> pair in Pending)
        {
            RemoteAvatar? avatar = CoopPlugin.Network?.GetAvatar(pair.Key);
            if (avatar == null || avatar.IsDead == pair.Value.Dead)
            {
                continue;
            }

            _deathsApplied++;
            avatar.SetDead(pair.Value.Dead, pair.Value.Position, pair.Value.Rotation);
        }

        foreach (int id in new List<int>(Pending.Keys))
        {
            if (CoopPlugin.Network?.GetAvatar(id) != null)
            {
                Pending.Remove(id);
            }
        }
    }
}

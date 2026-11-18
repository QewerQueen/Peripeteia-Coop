using System;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Client-to-host damage forwarding for NPCs.
///
/// The host simulates every NPC, so a bullet the client fires at an enemy only
/// exists on the client's machine. There the enemy is a frozen mirror whose
/// health is overwritten by the host's snapshots, so the local hit is thrown
/// away and the enemy never turns on the shooter. This module catches the local
/// damage the client's own game applies to an NPC and reports it to the host,
/// which applies it to the authoritative enemy with the client's puppet as the
/// dealer - that is what makes the enemy notice and fight back.
/// </summary>
internal static class NpcDamageSync
{
    private const int LogLimit = 12;

    private static int _sent;
    private static int _applied;
    private static int _sentLogged;
    private static int _appliedLogged;

    public static int Sent => _sent;

    public static int Applied => _applied;

    /// <summary>
    /// Called before the local game applies damage to a HealthScript. Returns
    /// true when the damage belonged to an NPC and was forwarded to the host.
    /// </summary>
    public static bool TryForward(
        HealthScript? health,
        bool headshot,
        int damage,
        Transform? dealer,
        Vector3 point
    )
    {
        if (CoopPlugin.Settings.Role != CoopRole.Client ||
            !CoopPlugin.AiEnabled ||
            !CoopPlugin.AiNpcDamageFromClient ||
            health == null ||
            damage <= 0 ||
            CoopPlugin.Network == null ||
            CoopPlugin.Network.ConnectedPeersCount == 0)
        {
            return false;
        }

        EnemyController? enemy;
        try
        {
            enemy = health.enemyC;
        }
        catch (Exception)
        {
            return false;
        }

        if (enemy == null || enemy.gameObject == null)
        {
            return false;
        }

        // Only the local player's damage is forwarded. NPC-versus-NPC fire is
        // simulated by the host already and must not be replayed.
        if (IsNpcDealer(dealer))
        {
            return false;
        }

        string key;
        try
        {
            key = EnemySync.KeyOf(enemy);
        }
        catch (Exception)
        {
            return false;
        }

        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        _sent++;
        CoopPlugin.Network.SendNpcDamage(key, damage, headshot, point);

        if (_sentLogged < LogLimit)
        {
            _sentLogged++;
            CoopPlugin.LogSource.LogInfo(
                $"[combat] forwarded {damage} damage to NPC '{SafeName(enemy)}' " +
                $"headshot={headshot} key='{key}' ({_sentLogged}/{LogLimit})"
            );
        }

        return true;
    }

    /// <summary>Host side: applies damage that a client reported against an NPC.</summary>
    public static void Apply(string key, int damage, bool headshot, Vector3 point, int fromId)
    {
        if (CoopPlugin.Settings.Role != CoopRole.Host ||
            !CoopPlugin.AiEnabled ||
            string.IsNullOrEmpty(key) ||
            damage <= 0)
        {
            return;
        }

        EnemyController? enemy = EnemySync.FindByKey(key);
        if (enemy == null || enemy.gameObject == null)
        {
            return;
        }

        HealthScript? health;
        try
        {
            health = enemy.health;
        }
        catch (Exception)
        {
            return;
        }

        if (health == null || health.dead)
        {
            return;
        }

        Transform? dealer = CoopPlugin.Network?.GetAvatarRoot(fromId);
        Vector3 hit = ResolvePoint(point, health, dealer);

        _applied++;
        try
        {
            health.TakeDMG(headshot, damage, dealer, false, hit, false);
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[combat] applying client NPC damage failed: " +
                $"{exception.GetType().Name}: {exception.Message}"
            );
            return;
        }

        // The game turns an NPC hostile when it is attacked, but the client's
        // puppet is not this machine's local player, so the enemy is also aimed
        // at it directly - otherwise it would keep ignoring the client.
        Provoke(enemy, dealer);

        if (_appliedLogged < LogLimit)
        {
            _appliedLogged++;
            CoopPlugin.LogSource.LogInfo(
                $"[combat] applied {damage} client damage to NPC '{SafeName(enemy)}' " +
                $"headshot={headshot} hp={SafeHp(health)} ({_appliedLogged}/{LogLimit})"
            );
        }
    }

    private static Vector3 ResolvePoint(Vector3 point, HealthScript health, Transform? dealer)
    {
        if (point != Vector3.zero)
        {
            return point;
        }

        try
        {
            if (health.centerOfMass != null)
            {
                return health.centerOfMass.position;
            }
        }
        catch (Exception)
        {
            // Fall through to the transform.
        }

        if (dealer != null)
        {
            return dealer.position;
        }

        return health.transform.position;
    }

    private static void Provoke(EnemyController enemy, Transform? dealer)
    {
        try
        {
            if (enemy.friendly)
            {
                enemy.becomeHostileToPlayer();
            }
        }
        catch (Exception)
        {
            // Some NPCs cannot be provoked; the damage itself still lands.
        }

        if (dealer == null)
        {
            return;
        }

        try
        {
            enemy.heardSound(dealer, 100.0f, false);
        }
        catch (Exception)
        {
            // Best effort.
        }

        try
        {
            enemy.raiseSuspicion(100.0f, dealer);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private static bool IsNpcDealer(Transform? dealer)
    {
        if (dealer == null)
        {
            return false;
        }

        try
        {
            return dealer.GetComponentInParent<EnemyController>() != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string SafeName(EnemyController enemy)
    {
        try
        {
            return enemy.CharacterName ?? "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string SafeHp(HealthScript health)
    {
        try
        {
            return health.HP.ToString();
        }
        catch (Exception)
        {
            return "?";
        }
    }
}

/// <summary>
/// Catches the damage the client's game applies to a mirrored NPC. The Vector3
/// overload is the funnel both other overloads call, so patching it alone covers
/// every caller without forwarding the same hit twice.
/// </summary>
[HarmonyPatch(
    typeof(HealthScript),
    "TakeDMG",
    new Type[] { typeof(bool), typeof(int), typeof(Transform), typeof(bool), typeof(Vector3), typeof(bool) }
)]
internal static class NpcDamageTakePatch
{
    private static void Prefix(
        HealthScript __instance,
        bool __0,
        int __1,
        Transform __2,
        Vector3 __4
    )
    {
        NpcDamageSync.TryForward(__instance, __0, __1, __2, __4);
    }
}

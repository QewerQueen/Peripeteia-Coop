using System;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Streams a lightweight "teammate state" line: current and maximum health, the
/// Marie skin index and the dead flag.
///
/// Health stays personal per player - each machine still owns and mutates its
/// own HP - but the partner needs to see it to draw the teammate panel, and the
/// skin index is what makes the two co-op characters look different instead of
/// being clones of one another.
/// </summary>
internal static class TeammateStateSync
{
    private const float SendInterval = 1.0f;

    private static int _lastHp = int.MinValue;
    private static int _lastMaxHp = int.MinValue;
    private static int _lastSkin = int.MinValue;
    private static bool _lastDead;
    private static float _nextSendTime;

    public static void Tick(PlayerMovement? localPlayer)
    {
        if (CoopPlugin.Settings.Role == CoopRole.Offline || CoopPlugin.Network == null)
        {
            return;
        }

        if (localPlayer == null || localPlayer.gameObject == null)
        {
            return;
        }

        HealthScript? health = localPlayer.GetComponent<HealthScript>() ??
                               localPlayer.GetComponentInParent<HealthScript>();
        if (health == null)
        {
            return;
        }

        int hp;
        int maxHp;
        bool dead;
        try
        {
            hp = health.HP;
            maxHp = health.maxHP;
            dead = health.dead;
        }
        catch (Exception)
        {
            return;
        }

        int skin = PlayerSkinSync.GetLocalVersion();

        bool changed = hp != _lastHp ||
                       maxHp != _lastMaxHp ||
                       dead != _lastDead ||
                       skin != _lastSkin;
        if (!changed && Time.unscaledTime < _nextSendTime)
        {
            return;
        }

        _lastHp = hp;
        _lastMaxHp = maxHp;
        _lastDead = dead;
        _lastSkin = skin;
        _nextSendTime = Time.unscaledTime + SendInterval;

        CoopPlugin.Network.SendPlayerState(hp, maxHp, skin, dead);
    }

    public static void Apply(int id, int hp, int maxHp, int skin, bool dead)
    {
        RemoteAvatar? avatar = CoopPlugin.Network?.GetAvatar(id);
        if (avatar == null)
        {
            return;
        }

        avatar.SetHealth(hp, maxHp, dead);
        avatar.SetSkin(skin);
    }
}

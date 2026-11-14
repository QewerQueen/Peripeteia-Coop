using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Development-only hierarchy dump. It answers two questions needed for the
/// next milestone: what the player object is made of, and whether the Marie
/// prefabs can be reached through playerSkins.playerPrefabs.
/// </summary>
[HarmonyPatch(typeof(PlayerMovement), "Update")]
internal static class PlayerHierarchyProbe
{
    private static bool _dumped;

    private static void Postfix(PlayerMovement __instance)
    {
        if (_dumped || !CoopPlugin.DumpHierarchy || __instance == null || Time.unscaledTime < 12.0f)
        {
            return;
        }

        _dumped = true;

        CoopPlugin.LogSource.LogInfo("[hier] === player hierarchy (depth 4) ===");
        DumpHierarchy(__instance.transform, 0, 4);

        CoopPlugin.LogSource.LogInfo("[hier] === playerSkins ===");
        DumpSkins(__instance);

        CoopPlugin.LogSource.LogInfo("[hier] === matching resources ===");
        DumpResources();
    }

    private static void DumpHierarchy(Transform root, int depth, int maxDepth)
    {
        var builder = new StringBuilder();
        builder.Append("[hier] ");
        builder.Append(' ', depth * 2);
        builder.Append(root.name);
        builder.Append(" [");

        var components = root.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            Component component = components[i];
            builder.Append(component != null ? component.GetType().Name : "null");
        }

        builder.Append(']');
        CoopPlugin.LogSource.LogInfo(builder.ToString());

        if (depth >= maxDepth)
        {
            return;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            DumpHierarchy(root.GetChild(i), depth + 1, maxDepth);
        }
    }

    private static void DumpSkins(PlayerMovement player)
    {
        playerSkins skins = player.GetComponent<playerSkins>();
        if (skins == null)
        {
            CoopPlugin.LogSource.LogInfo("[hier] playerSkins component not found.");
            return;
        }

        Type type = skins.GetType();
        CoopPlugin.LogSource.LogInfo($"[hier] playerSkins type={type.FullName}");

        try
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                CoopPlugin.LogSource.LogInfo($"[hier] field {field.FieldType.Name} {field.Name}");
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                CoopPlugin.LogSource.LogInfo($"[hier] property {property.PropertyType.Name} {property.Name}");
            }
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning($"[hier] reflection failed: {exception.GetType().Name}: {exception.Message}");
        }

        DumpPrefabValue(skins, type, "playerPrefabs");
    }

    private static void DumpPrefabValue(playerSkins skins, Type type, string name)
    {
        try
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                object? value = field.GetValue(skins);
                CoopPlugin.LogSource.LogInfo($"[hier] field {name} value={Describe(value)}");
                DumpPrefabList(value);
                return;
            }

            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead)
            {
                object? value = property.GetValue(skins);
                CoopPlugin.LogSource.LogInfo($"[hier] property {name} value={Describe(value)}");
                DumpPrefabList(value);
                return;
            }

            CoopPlugin.LogSource.LogInfo($"[hier] {name} not exposed through reflection.");
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[hier] reading {name} failed: {exception.GetType().Name}: {exception.Message}"
            );
        }
    }

    private static void DumpPrefabList(object? value)
    {
        if (value is not Il2CppSystem.Collections.Generic.List<Transform> list)
        {
            return;
        }

        CoopPlugin.LogSource.LogInfo($"[hier] playerPrefabs count={list.Count}");
        for (int i = 0; i < list.Count; i++)
        {
            Transform? prefab = list[i];
            CoopPlugin.LogSource.LogInfo($"[hier]   playerPrefabs[{i}]={(prefab != null ? prefab.name : "null")}");
        }
    }

    private static string Describe(object? value)
    {
        if (value == null)
        {
            return "null";
        }

        try
        {
            return $"{value.GetType().FullName} ({value})";
        }
        catch
        {
            return value.GetType().FullName ?? value.GetType().Name;
        }
    }

    private static void DumpResources()
    {
        var objects = Resources.FindObjectsOfTypeAll<GameObject>();
        int matched = 0;

        for (int i = 0; i < objects.Length && matched < 120; i++)
        {
            GameObject gameObject = objects[i];
            if (gameObject == null)
            {
                continue;
            }

            string name = gameObject.name;
            if (name.IndexOf("marie", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("body", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            matched++;
            CoopPlugin.LogSource.LogInfo(
                $"[hier] resource '{name}' activeSelf={gameObject.activeSelf} scene='{gameObject.scene.name}'"
            );
        }

        CoopPlugin.LogSource.LogInfo($"[hier] matching resources={matched}, total={objects.Length}");
    }
}

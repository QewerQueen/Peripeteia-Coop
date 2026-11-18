using UnityEngine;

namespace PeripeteiaCoop;

/// <summary>
/// Independent Unity heartbeat for the network layer. PlayerMovement.Update is
/// not called while a level is loading, which used to delay the handshake until
/// the connection timed out. This component is owned by BepInEx, survives scene
/// loads and finds the local player lazily.
/// </summary>
public sealed class CoopRunner : MonoBehaviour
{
    private PlayerMovement? _localPlayer;
    private float _nextFindTime;
    private bool _autoLoadAttempted;
    private float _nextAutoLoadTime;
    private float _lastTimeScale = 1.0f;

    private void Update()
    {
        if (_localPlayer == null || !_localPlayer.isActiveAndEnabled)
        {
            if (Time.unscaledTime >= _nextFindTime)
            {
                _nextFindTime = Time.unscaledTime + 1.0f;
                _localPlayer = FindObjectOfType<PlayerMovement>();
            }
        }

        if (CoopPlugin.ProbeEnabled &&
            Mathf.Abs(Time.timeScale - _lastTimeScale) > 0.001f)
        {
            _lastTimeScale = Time.timeScale;
            CoopPlugin.LogSource.LogInfo($"[pause] timeScale={Time.timeScale:F2}");
        }

        TryAutoLoadSave();
        SaveLoadSync.TickAutoStart();
        CoopPlugin.Tick(_localPlayer);
    }

    private void LateUpdate()
    {
        CoopPlugin.LateTick();
    }

    private void TryAutoLoadSave()
    {
        if (_autoLoadAttempted || string.IsNullOrEmpty(CoopPlugin.Settings.LoadSaveName))
        {
            return;
        }

        if (Time.unscaledTime < _nextAutoLoadTime)
        {
            return;
        }

        _nextAutoLoadTime = Time.unscaledTime + 1.0f;

        if (SaveLoadSync.TryStartFromMainMenu(CoopPlugin.Settings.LoadSaveName))
        {
            _autoLoadAttempted = true;
            CoopPlugin.LogSource.LogInfo(
                $"[test] Auto-loading save '{CoopPlugin.Settings.LoadSaveName}' from the main menu."
            );
        }
    }

    private void OnGUI()
    {
        if (!CoopPlugin.ShowStatusOverlay || CoopPlugin.Network == null)
        {
            return;
        }

        float width = Mathf.Min(760.0f, Screen.width - 20.0f);
        GUI.Box(new Rect(10.0f, 10.0f, width, 26.0f), CoopPlugin.Network.StatusText);
    }
}

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

        CoopPlugin.Tick(_localPlayer);
    }
}

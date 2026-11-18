using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeripeteiaCoop;

/// <summary>
/// The in-game "Peripeteia Coop" entry point. Adds a button to the game's own
/// main menu (cloned from an existing button so it keeps the game's style) and
/// draws a small IMGUI panel with the LAN host list, a "host game" action and a
/// "download the host save" button.
///
/// IMGUI is used for the panel itself on purpose: it needs no font, prefab or
/// canvas wiring, so it works no matter how the menu scene is built. The menu
/// button is the only part that touches uGUI.
/// </summary>
public sealed class CoopMenu : MonoBehaviour
{
    private static CoopMenu? _instance;

    private bool _open;
    private bool _buttonBuilt;
    private bool _menuPresent;
    private float _nextMenuCheck;
    private string _status = "Ready.";

    private GUIStyle? _boxStyle;
    private GUIStyle? _titleStyle;
    private GUIStyle? _labelStyle;
    private GUIStyle? _buttonStyle;
    private GUIStyle? _hostButtonStyle;
    private bool _panelLogged;

    /// <summary>Open/close the panel from the menu button or a hotkey.</summary>
    public static void Toggle()
    {
        if (_instance != null)
        {
            _instance._open = !_instance._open;
        }
    }

    /// <summary>
    /// Close the panel from elsewhere (for example when a freshly downloaded save
    /// starts the run, so the panel does not linger over the level).
    /// </summary>
    public static void ClosePanel()
    {
        if (_instance != null)
        {
            _instance._open = false;
        }
    }

    private void Awake()
    {
        _instance = this;

        // -coop-menu-test: open the panel immediately, for log-based testing of
        // the IMGUI draw (GUI.Button/GUI.Label availability) without input.
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (string.Equals(argument, "-coop-menu-test", StringComparison.OrdinalIgnoreCase))
            {
                _open = true;
                CoopPlugin.LogSource.LogInfo("[menu] -coop-menu-test: opening the panel for a draw test.");
                break;
            }
        }

        // In a plain launch the player browses for hosts from the menu. The host
        // path (command line or the menu button) advertises instead.
        if (CoopPlugin.Settings.Role == CoopRole.Offline)
        {
            LanDiscovery.Listen();
        }
    }

    private void Update()
    {
        LanDiscovery.Tick();

        if (CoopPlugin.IsCoopActive && CoopPlugin.Network != null)
        {
            _status = CoopPlugin.Network.StatusText;
        }

        if (Time.unscaledTime < _nextMenuCheck)
        {
            return;
        }

        _nextMenuCheck = Time.unscaledTime + 1.0f;
        _menuPresent = FindObjectOfType<MainMenu>() != null;

        if (_menuPresent && !_buttonBuilt)
        {
            TryBuildButton();
        }
    }

    // ------------------------------------------------------------------
    // uGUI button
    // ------------------------------------------------------------------

    private void TryBuildButton()
    {
        MainMenu? menu = FindObjectOfType<MainMenu>();
        if (menu == null)
        {
            return;
        }

        GameObject? anchor = FindAnchor(menu);
        if (anchor == null)
        {
            return;
        }

        Button? template = FindTemplateButton(anchor);
        if (template == null)
        {
            return;
        }

        GameObject clone;
        try
        {
            clone = Instantiate(template.gameObject, template.transform.parent);
        }
        catch (Exception exception)
        {
            _buttonBuilt = true;
            CoopPlugin.LogSource.LogWarning(
                $"[menu] could not clone a menu button: {exception.GetType().Name}: {exception.Message}"
            );
            return;
        }

        clone.name = "PeripeteiaCoopMenuButton";
        clone.SetActive(true);

        Button? button = clone.GetComponent<Button>();
        if (button != null)
        {
            // A clone copies the template's listeners (here the Quit button's
            // own action), so replace the whole event instead of only adding
            // ours: otherwise clicking the new button would still ask to quit.
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener((UnityAction)OnMenuButtonClicked);
        }

        SetLabel(clone, "COOP");
        PositionBelow(clone);

        _buttonBuilt = true;
        LogHierarchy(clone);
    }

    private void OnMenuButtonClicked()
    {
        _open = !_open;
        CoopPlugin.LogSource.LogInfo(
            $"[menu] panel {(_open ? "opened" : "closed")} from the menu button."
        );
    }

    private static GameObject? FindAnchor(MainMenu menu)
    {
        GameObject?[] candidates = { menu.startScreenCanvas, menu.primaryMenu };
        foreach (GameObject? candidate in candidates)
        {
            if (candidate != null && FindTemplateButton(candidate) != null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static Button? FindTemplateButton(GameObject anchor)
    {
        Button? last = null;
        int count = anchor.transform.childCount;
        for (int i = 0; i < count; i++)
        {
            Button? button = anchor.transform.GetChild(i).GetComponent<Button>();
            if (button != null)
            {
                last = button;
            }
        }

        return last;
    }

    private static void SetLabel(GameObject root, string label)
    {
        Text? text = root.GetComponentInChildren<Text>(true);
        if (text != null)
        {
            text.text = label;
            return;
        }

        TextMeshProUGUI? tmp = root.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp != null)
        {
            tmp.text = label;
        }
    }

    private static void PositionBelow(GameObject clone)
    {
        RectTransform? cloneRect = clone.GetComponent<RectTransform>();
        Transform? parent = clone.transform.parent;
        if (cloneRect == null || parent == null)
        {
            return;
        }

        // If the parent lays its children out, it places the clone for us.
        if (parent.GetComponent<VerticalLayoutGroup>() != null ||
            parent.GetComponent<HorizontalLayoutGroup>() != null ||
            parent.GetComponent<GridLayoutGroup>() != null)
        {
            return;
        }

        // Otherwise drop it clearly below the lowest button, so it can never
        // sit on top of the exit button and steal its click.
        float lowest = float.MaxValue;
        float x = cloneRect.anchoredPosition.x;
        bool found = false;
        int count = parent.childCount;
        for (int i = 0; i < count; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.gameObject == clone)
            {
                continue;
            }

            Button? button = child.GetComponent<Button>();
            RectTransform? rect = button != null ? child.GetComponent<RectTransform>() : null;
            if (rect == null)
            {
                continue;
            }

            float bottom = rect.anchoredPosition.y - (rect.rect.height * 0.5f);
            if (bottom < lowest)
            {
                lowest = bottom;
            }

            x = rect.anchoredPosition.x;
            found = true;
        }

        if (!found)
        {
            return;
        }

        float height = cloneRect.rect.height > 1.0f ? cloneRect.rect.height : 40.0f;
        cloneRect.anchoredPosition = new Vector2(x, lowest - 16.0f - (height * 0.5f));
    }

    private static void LogHierarchy(GameObject clone)
    {
        Transform? parent = clone.transform.parent;
        if (parent == null)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append($"[menu] button added under '{parent.name}'. Siblings: ");
        int count = parent.childCount;
        for (int i = 0; i < count; i++)
        {
            Transform child = parent.GetChild(i);
            RectTransform? rect = child.GetComponent<RectTransform>();
            Button? button = child.GetComponent<Button>();
            string pos = rect != null
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F0},{1:F0})", rect.anchoredPosition.x, rect.anchoredPosition.y)
                : "?";
            builder.Append($"'{child.name}'{pos}{(button != null ? "[B]" : string.Empty)} ");
        }

        CoopPlugin.LogSource.LogInfo(builder.ToString());
    }

    // ------------------------------------------------------------------
    // IMGUI panel
    // ------------------------------------------------------------------

    private void OnGUI()
    {
        if (!_open)
        {
            // Fallback: if the game's menu could not be cloned, still offer a way in.
            if (_menuPresent && !_buttonBuilt)
            {
                EnsureStyles();
                if (GUI.Button(new Rect(12.0f, 12.0f, 160.0f, 30.0f), "COOP", _buttonStyle))
                {
                    _open = true;
                }
            }

            return;
        }

        EnsureStyles();

        float width = 560.0f;
        float height = 520.0f;
        var rect = new Rect(
            (Screen.width - width) / 2.0f,
            (Screen.height - height) / 2.0f,
            width,
            height
        );

        if (!_panelLogged)
        {
            _panelLogged = true;
            CoopPlugin.LogSource.LogInfo(
                $"[menu] drawing the panel at ({rect.x:F0},{rect.y:F0}) {width:F0}x{height:F0}, " +
                $"screen {Screen.width}x{Screen.height}."
            );
        }

        // GUILayout is stripped from this IL2CPP build ("Method unstripping
        // failed"), so the panel is drawn with explicit rects and a manual y
        // cursor instead.
        GUI.Box(rect, GUIContent.none, _boxStyle);

        float x = rect.x + 20.0f;
        float w = rect.width - 40.0f;
        float y = rect.y + 16.0f;

        GUI.Label(new Rect(x, y, w, 30.0f), "PERIPETEIA COOP", _titleStyle);
        y += 34.0f;
        GUI.Label(new Rect(x, y, w, 22.0f), _status, _labelStyle);
        y += 28.0f;

        if (!CoopPlugin.IsCoopActive)
        {
            DrawBrowse(x, y, w);
        }
        else
        {
            DrawSession(x, y, w);
        }
    }

    private void DrawBrowse(float x, float y, float w)
    {
        if (GUI.Button(new Rect(x, y, w, 36.0f), "Host game", _buttonStyle))
        {
            CoopPlugin.StartHostSession(CoopPlugin.DefaultPlayerName);
            _status = "Hosting started. Waiting for a second player...";
        }

        y += 44.0f;
        GUI.Label(new Rect(x, y, w, 22.0f), "Hosts on the local network:", _labelStyle);
        y += 26.0f;

        IReadOnlyList<LanDiscovery.HostEntry> hosts = LanDiscovery.Hosts;
        if (hosts.Count == 0)
        {
            GUI.Label(
                new Rect(x, y, w, 44.0f),
                "  Nobody found yet. Make sure the host runs the mod and is on the same network.",
                _labelStyle
            );
            y += 50.0f;
        }
        else
        {
            int shown = Math.Min(hosts.Count, 8);
            for (int i = 0; i < shown; i++)
            {
                LanDiscovery.HostEntry host = hosts[i];
                string label =
                    $"{host.Name}  -  {host.Scene}  -  {host.Address}:{host.Port}  -  saves: {host.SaveCount}";
                if (GUI.Button(new Rect(x, y, w, 32.0f), label, _hostButtonStyle))
                {
                    CoopPlugin.JoinSession(host.Address, host.Port, CoopPlugin.DefaultPlayerName);
                    _status = $"Connecting to '{host.Name}' ({host.Address}:{host.Port})...";
                    _open = false;
                    return;
                }

                y += 36.0f;
            }

            if (hosts.Count > shown)
            {
                GUI.Label(new Rect(x, y, w, 22.0f), $"  ...and {hosts.Count - shown} more", _labelStyle);
                y += 26.0f;
            }

            y += 6.0f;
        }

        float half = (w - 10.0f) / 2.0f;
        if (GUI.Button(new Rect(x, y, half, 34.0f), "Refresh", _buttonStyle))
        {
            _status = $"Hosts found: {LanDiscovery.Hosts.Count}.";
        }

        if (GUI.Button(new Rect(x + half + 10.0f, y, half, 34.0f), "Close", _buttonStyle))
        {
            _open = false;
        }
    }

    private void DrawSession(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, 22.0f), $"Role: {CoopPlugin.Settings.Role}", _labelStyle);
        y += 24.0f;
        GUI.Label(new Rect(x, y, w, 22.0f), $"Player: {CoopPlugin.Settings.PlayerName}", _labelStyle);
        y += 24.0f;
        GUI.Label(new Rect(x, y, w, 22.0f), CoopPlugin.Network?.StatusText ?? string.Empty, _labelStyle);
        y += 30.0f;

        if (CoopPlugin.Settings.Role == CoopRole.Client)
        {
            if (GUI.Button(new Rect(x, y, w, 36.0f), "Download and start the host save", _buttonStyle))
            {
                CoopPlugin.Network?.SendSaveRequest();
                _status = "Downloading the host save; the run will start when it arrives...";
            }

            y += 44.0f;

            if (SaveTransfer.ReceivedCount > 0)
            {
                GUI.Label(
                    new Rect(x, y, w, 22.0f),
                    $"Downloaded: '{SaveTransfer.LastReceivedName}'",
                    _labelStyle
                );
                y += 24.0f;
                string hint = CoopPlugin.AutoStartDownloadedSave
                    ? "Starting the run from the downloaded save..."
                    : "Open the game's load menu and pick that save to start together with the host.";
                GUI.Label(new Rect(x, y, w, 44.0f), hint, _labelStyle);
                y += 50.0f;
            }
            else if (SaveTransfer.IsReceiving)
            {
                GUI.Label(new Rect(x, y, w, 22.0f), "Downloading the save...", _labelStyle);
                y += 26.0f;
            }
        }
        else
        {
            GUI.Label(new Rect(x, y, w, 22.0f), $"Saves sent to the client: {SaveTransfer.SentCount}", _labelStyle);
            y += 24.0f;
            GUI.Label(
                new Rect(x, y, w, 44.0f),
                "Save the game so the client gets the current host save.",
                _labelStyle
            );
            y += 50.0f;
        }

        if (GUI.Button(new Rect(x, y, w, 34.0f), "Close", _buttonStyle))
        {
            _open = false;
        }
    }

    private void EnsureStyles()
    {
        if (_boxStyle != null)
        {
            return;
        }

        _boxStyle = new GUIStyle(GUI.skin.box);

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            wordWrap = true,
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 15,
            alignment = TextAnchor.MiddleCenter,
            fixedHeight = 34.0f,
        };

        _hostButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleLeft,
            fixedHeight = 32.0f,
        };
    }
}

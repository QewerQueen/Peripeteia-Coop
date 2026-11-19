using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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

    // Manual "connect by IP" entry, for networks where the LAN beacon does not
    // get through (RadminVPN, Hamachi, plain internet with a forwarded port).
    // GUI.TextField is stripped from this IL2CPP build ("NotSupportedException"),
    // so the field is drawn by hand and keys are read from Event.current instead.
    private string _manualAddress = string.Empty;
    private bool _ipFocused;

    // While the panel is open the game's own uGUI input is muted, so a click on
    // the panel cannot fall through to a menu button behind it (for example the
    // "COOP" button, which would otherwise re-open the panel right after Close).
    private BaseInputModule? _blockedModule;
    private bool _blockedNavigation;

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
        SyncGameInput();

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

    /// <summary>
    /// Mutes the game's uGUI input while the panel is open and restores it when
    /// the panel closes. Without this a click on the IMGUI panel also reaches the
    /// menu button drawn underneath it (uGUI and IMGUI both see the mouse), which
    /// made "Close" look broken because the button below it re-opened the panel.
    /// </summary>
    private void SyncGameInput()
    {
        EventSystem? system = EventSystem.current;

        if (_open)
        {
            if (system == null || _blockedModule != null)
            {
                return;
            }

            _blockedModule = system.currentInputModule;
            _blockedNavigation = system.sendNavigationEvents;

            if (_blockedModule != null)
            {
                _blockedModule.enabled = false;
            }

            system.sendNavigationEvents = false;
            CoopPlugin.LogSource.LogInfo(
                "[menu] panel open: the game's menu input is muted while the panel is up."
            );
        }
        else if (_blockedModule != null)
        {
            _blockedModule.enabled = true;

            if (system != null)
            {
                system.sendNavigationEvents = _blockedNavigation;
            }

            _blockedModule = null;
            CoopPlugin.LogSource.LogInfo(
                "[menu] panel closed: the game's menu input is restored."
            );
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
            _ipFocused = false;

            // Fallback: if the game's menu could not be cloned, still offer a way in.
            if (_menuPresent && !_buttonBuilt)
            {
                EnsureStyles();
                if (PanelButton(new Rect(12.0f, 12.0f, 160.0f, 30.0f), "COOP"))
                {
                    _open = true;
                }
            }

            return;
        }

        EnsureStyles();

        float width = 560.0f;
        float height = 620.0f;
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
        if (PanelButton(new Rect(x, y, w, 36.0f), "Host game"))
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
                "  Nobody found yet. Make sure the host runs the mod and is on the same network. " +
                "On a VPN such as RadminVPN, use the field below with the host's VPN IP.",
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
                if (PanelButton(new Rect(x, y, w, 32.0f), label, _hostButtonStyle))
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

        GUI.Label(new Rect(x, y, w, 22.0f), "Connect by IP (RadminVPN / Hamachi / internet):", _labelStyle);
        y += 24.0f;
        float connectW = 150.0f;
        float fieldW = w - connectW - 10.0f;
        DrawAddressField(new Rect(x, y, fieldW, 30.0f));
        if (PanelButton(new Rect(x + fieldW + 10.0f, y, connectW, 30.0f), "Connect by IP"))
        {
            ConnectManual();
        }

        y += 34.0f;
        GUI.Label(
            new Rect(x, y, w, 22.0f),
            _ipFocused
                ? "Type the host address (for example 26.12.34.56:7777), then press Enter."
                : "Click the box above, type the host address (for example 26.12.34.56:7777), then press Enter.",
            _labelStyle
        );
        y += 30.0f;

        float half = (w - 10.0f) / 2.0f;
        if (PanelButton(new Rect(x, y, half, 34.0f), "Refresh"))
        {
            _status = $"Hosts found: {LanDiscovery.Hosts.Count}.";
        }

        if (PanelButton(new Rect(x + half + 10.0f, y, half, 34.0f), "Close"))
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
            if (PanelButton(new Rect(x, y, w, 36.0f), "Download and start the host save"))
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

        if (PanelButton(new Rect(x, y, w, 34.0f), "Close"))
        {
            _open = false;
        }
    }

    /// <summary>
    /// Draws one of the panel's buttons and reports a click.
    ///
    /// GUI.Button is deliberately not used here: in this IL2CPP build its
    /// hot-control bookkeeping is unreliable once other controls have been
    /// drawn on the panel (clicking the first button, "Host game", worked, but
    /// clicking "Close" never fired), so the frame is drawn with GUI.Box and the
    /// click is taken straight from the mouse-up event instead.
    /// </summary>
    private bool PanelButton(Rect rect, string text, GUIStyle? style = null)
    {
        Event current = Event.current;
        bool hover = rect.Contains(current.mousePosition);

        Color previous = GUI.color;
        GUI.color = hover ? new Color(0.72f, 0.88f, 1.0f, 1.0f) : Color.white;
        GUI.Box(rect, text, style ?? _buttonStyle);
        GUI.color = previous;

        if (current.type == EventType.MouseUp && hover)
        {
            current.Use();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Draws the free-text "connect by IP" field by hand. GUI.TextField (and
    /// GUILayout) is stripped from this IL2CPP build and throws
    /// NotSupportedException, so the box, the text, the caret and the key
    /// handling are all done with the IMGUI primitives that are present.
    /// </summary>
    private void DrawAddressField(Rect rect)
    {
        Event current = Event.current;

        if (current.type == EventType.MouseDown)
        {
            if (rect.Contains(current.mousePosition))
            {
                _ipFocused = true;
                current.Use();
            }
            else
            {
                _ipFocused = false;
            }
        }

        // Field frame + the address itself (a blinking caret shows where typing goes).
        GUI.Box(rect, GUIContent.none, _boxStyle);
        string shown = _manualAddress;
        if (_ipFocused && Mathf.FloorToInt(Time.unscaledTime * 2.0f) % 2 == 0)
        {
            shown += "|";
        }

        GUI.Label(
            new Rect(rect.x + 8.0f, rect.y + 6.0f, rect.width - 16.0f, rect.height - 10.0f),
            shown,
            _labelStyle
        );

        if (!_ipFocused || current.type != EventType.KeyDown)
        {
            return;
        }

        if (current.keyCode == KeyCode.Backspace)
        {
            if (_manualAddress.Length > 0)
            {
                _manualAddress = _manualAddress.Substring(0, _manualAddress.Length - 1);
            }

            current.Use();
        }
        else if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
        {
            ConnectManual();
            current.Use();
        }
        else if (current.keyCode == KeyCode.Escape)
        {
            _ipFocused = false;
            current.Use();
        }
        else if (current.keyCode == KeyCode.V &&
                 (current.modifiers & EventModifiers.Control) != 0)
        {
            TryPaste();
            current.Use();
        }
        else if (current.character != '\0' && !char.IsControl(current.character))
        {
            if (_manualAddress.Length < 40)
            {
                _manualAddress += current.character;
            }

            current.Use();
        }
    }

    /// <summary>
    /// Ctrl+V support for the address field. GUIUtility.systemCopyBuffer is a
    /// plain native call (unlike GUI.TextField), but it is guarded anyway so a
    /// stripped build cannot break the panel.
    /// </summary>
    private void TryPaste()
    {
        try
        {
            string text = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            text = text.Trim();
            _manualAddress = text.Length > 40 ? text.Substring(0, 40) : text;
        }
        catch (Exception exception)
        {
            CoopPlugin.LogSource.LogWarning(
                $"[menu] clipboard unavailable ({exception.GetType().Name}); type the address by hand."
            );
        }
    }

    /// <summary>Joins the host entered in the "connect by IP" field.</summary>
    private void ConnectManual()
    {
        string text = _manualAddress.Trim();
        if (text.Length == 0)
        {
            _status = "Enter the host address, for example 26.12.34.56:7777.";
            return;
        }

        string address = text;
        int port = 7777;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && colon < text.Length - 1)
        {
            if (int.TryParse(text.Substring(colon + 1), out int parsed) &&
                parsed > 0 &&
                parsed <= 65535)
            {
                port = parsed;
                address = text.Substring(0, colon);
            }
        }

        if (address.Length == 0)
        {
            _status = "Enter the host address, for example 26.12.34.56:7777.";
            return;
        }

        CoopPlugin.JoinSession(address, port, CoopPlugin.DefaultPlayerName);
        _status = $"Connecting to {address}:{port}...";
        _ipFocused = false;
        _open = false;
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

using RedLoader;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using RedLoader.Utils;
using Sons.Ai.Vail;
using Sons.Gameplay.GPS;
using SonsSdk;
using TheForest.UI.Multiplayer;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Map;

public class Map : SonsMod
{
    private const string GpsActionName = "GpsTracker";
    private const string GpsActionMap = "default";
    private const float ZoomStep = 1.25f;
    private const float MaxZoom = 12f;
    private const float ViewFraction = 0.92f;
    private const float BackgroundAlpha = 0.85f;
    private const float MarkerSize = 34f;
    private const float ArrowSize = 44f;
    private const float OtherArrowSize = 38f;
    private const int LabelFontSize = 18;
    private const string Kelvin = "Kelvin";
    private const string Virginia = "Virginia";
    private const string Player = "Player";
    private const string Locator = "Locator";
    private const float LocatorSize = 30f;
    private const float HoldTime = 0.6f;
    private const string GpsTapInteraction = "tap(duration=0.45)";
    private const float WaypointSize = 30f;
    private const string WaypointIconName = "MapMarkerIcon";
    private const float WaypointLifetime = 300f;
    private const string ChatTag = "[Map] ";
    private const string SelfKey = "<self>";
    private static readonly Regex SetRx = new(@"^(.+?) set a waypoint at (-?\d+), (-?\d+)");
    private static readonly Regex ClearRx = new(@"^(.+?) cleared the waypoint");
    private static readonly string[] BlockedActions = { "MouseX", "MouseY", "PrimaryAction", "SecondaryAction" };
    private static readonly Color[] Palette =
    {
        new(1f, 0.25f, 0.25f, 1f),
        new(0.3f, 1f, 0.3f, 1f),
        new(1f, 0.3f, 1f, 1f),
        new(1f, 0.55f, 0.1f, 1f),
        new(0f, 0.85f, 1f, 1f),
        new(0.6f, 0.4f, 1f, 1f),
        new(1f, 1f, 1f, 1f),
        new(1f, 0.6f, 0.75f, 1f)
    };

    private static readonly Color YouColor = new(1f, 0.961f, 0f, 1f);
    private static readonly Color OtherColor = new(0f, 0.85f, 1f, 1f);

    private class Marker
    {
        public string Kind;
        public Transform Target;
        public RectTransform Rect;
        public RectTransform Arrow;
        public Text Label;
        public string Signature;
    }

    private class Waypoint
    {
        public string Owner;
        public Vector2 Xz;
        public float Expires;
        public Color Color;
        public RectTransform MapRect;
        public RectTransform WorldRect;
        public CanvasGroup WorldGroup;
        public Text Distance;
    }

    private struct Found
    {
        public string Kind;
        public Transform Target;
        public string Name;
        public Texture Icon;
        public Color IconColor;
        public Texture Outline;
        public Color OutlineColor;
        public bool HasBackground;
        public Color BackgroundColor;
        public string Signature;
    }

    private static Canvas _canvas;
    private static RectTransform _view;
    private static RawImage _map;
    private static RectTransform _player;
    private static readonly List<Marker> Markers = new();

    private static Texture2D _mapTex;
    private static Rect _mapUv = new(0f, 0f, 1f, 1f);
    private static Vector2 _worldSize = new(2000f, 2000f);
    private static Sprite _arrowSprite;
    private static Font _font;
    private static Texture _kelvinTex;
    private static Texture _heartTex;
    private static Texture _circleTex;

    private static InputAction _gpsAction;
    private static float _zoom = MaxZoom;
    private static bool _open;
    private static float _nextBindingCheck;
    private static float _nextScan;
    private static float _nextIconLookup;
    private static bool _warnedBinding;
    private static bool _warnedPlayers;
    private static bool _warnedRescan;
    private static bool _warnedLocators;
    private static readonly HashSet<string> WarnedCreate = new();
    private static string _lastSummary = string.Empty;
    private static bool _tookM;
    private static int _overrideIndex = -1;
    private static int _tapIndex = -1;
    private static bool _gpsMode;
    private static float _mDownAt = -1f;
    private static bool _holdHandled;
    private static string _configPath;
    private static readonly Dictionary<string, Waypoint> Waypoints = new();
    private static readonly List<InputAction> Blocked = new();
    private static Canvas _worldCanvas;
    private static Texture _waypointTex;
    private static ChatBox _chat;
    private static Rect _lastView = new(0f, 0f, 1f, 1f);
    private static bool _warnedChat;
    private static float _nextChatPoll;
    private static float _nextChatLookup;
    private static readonly Dictionary<IntPtr, string> SeenRows = new();
    private static bool _warnedBlock;

    public Map()
    {
        OnUpdateCallback = OnUpdate;
    }

    protected override void OnSdkInitialized()
    {
        _configPath = Path.Combine(LoaderEnvironment.UserDataDirectory, "Map.txt");
        Load();
        RLog.Msg($"Map loaded. M opens the {(_gpsMode ? "GPS" : "map")}, hold M to switch, scroll to zoom");
    }

    private void OnUpdate()
    {
        if (Time.unscaledTime >= _nextBindingCheck)
        {
            _nextBindingCheck = Time.unscaledTime + 1f;
            ReleaseM();
        }

        var gameplay = _gpsAction != null && _gpsAction.enabled && LocalPlayer.Transform;
        PollChat();
        UpdateWaypoints();

        if (_open && !gameplay)
        {
            SetOpen(false, false);
            return;
        }

        HandleM(gameplay);

        if (!_open) return;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        HandleZoom();
        HandleClick();

        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 1f;
            try
            {
                Rescan();
            }
            catch (Exception e)
            {
                if (!_warnedRescan)
                {
                    _warnedRescan = true;
                    RLog.Error($"Map rescan failed: {e}");
                }
            }
        }

        Layout();
    }

    private static void HandleM(bool gameplay)
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        var key = kb.mKey;

        if (key.wasPressedThisFrame)
        {
            _mDownAt = gameplay ? Time.unscaledTime : -1f;
            _holdHandled = false;
        }

        if (_mDownAt >= 0f && !_holdHandled && key.isPressed && Time.unscaledTime - _mDownAt >= HoldTime)
        {
            _holdHandled = true;
            SetGpsMode(!_gpsMode);
        }

        if (key.wasReleasedThisFrame)
        {
            var tap = _mDownAt >= 0f && !_holdHandled;
            _mDownAt = -1f;
            if (tap && gameplay && !_gpsMode)
                SetOpen(!_open);
        }
    }

    private static void SetGpsMode(bool gps)
    {
        _gpsMode = gps;
        Save();
        if (gps)
        {
            if (_open) SetOpen(false);
            RestoreGpsBinding();
        }
        else
        {
            RemoveGpsTap();
        }
        ReleaseM();
        SonsTools.ShowMessage(gps ? "M now raises the GPS. Hold M to switch back to the map" : "M now opens the map. Hold M to switch to the GPS");
        RLog.Msg($"Map mode: M opens the {(gps ? "GPS" : "map")}");
    }

    private static void RestoreGpsBinding()
    {
        try
        {
            if (_gpsAction == null || _overrideIndex < 0) return;
            InputActionRebindingExtensions.RemoveBindingOverride(_gpsAction, _overrideIndex);
            _overrideIndex = -1;
        }
        catch (Exception e)
        {
            RLog.Warning($"Map could not give M back to the GPS: {e.Message}");
        }
    }

    private static void RemoveGpsTap()
    {
        try
        {
            if (_gpsAction == null || _tapIndex < 0) return;
            InputActionRebindingExtensions.RemoveBindingOverride(_gpsAction, _tapIndex);
            _tapIndex = -1;
        }
        catch (Exception e)
        {
            RLog.Warning($"Map could not reset the GPS key: {e.Message}");
        }
    }

    private static void ApplyGpsTap(InputAction action, Keyboard kb)
    {
        if (_tapIndex >= 0) return;
        var index = InputActionRebindingExtensions.GetBindingIndexForControl(action, kb.mKey);
        if (index < 0) return;
        var binding = new InputBinding();
        binding.overrideInteractions = GpsTapInteraction;
        InputActionRebindingExtensions.ApplyBindingOverride(action, index, binding);
        _tapIndex = index;
    }

    private static void ReleaseM()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            var actions = InputSystem.ListEnabledActions();
            InputAction found = null;
            for (var i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a != null && a.name == GpsActionName && a.actionMap != null && a.actionMap.name == GpsActionMap)
                {
                    found = a;
                    break;
                }
            }
            if (found == null) return;
            if (_gpsAction == null || _gpsAction.Pointer != found.Pointer) _tapIndex = -1;
            _gpsAction = found;
            if (_gpsMode)
            {
                ApplyGpsTap(found, kb);
                return;
            }

            var index = InputActionRebindingExtensions.GetBindingIndexForControl(found, kb.mKey);
            if (index < 0) return;
            InputActionRebindingExtensions.ApplyBindingOverride(found, index, string.Empty);
            _overrideIndex = index;
            if (!_tookM)
            {
                _tookM = true;
                RLog.Msg("Map moved M from the GPS tracker to the full map");
            }
        }
        catch (Exception e)
        {
            if (_warnedBinding) return;
            _warnedBinding = true;
            RLog.Warning($"Map could not take over M: {e.Message}");
        }
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                Save();
                return;
            }
            foreach (var line in File.ReadAllLines(_configPath))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var k = line[..i].Trim();
                var v = line[(i + 1)..].Trim();
                if (k.Equals("Mode", StringComparison.OrdinalIgnoreCase))
                    _gpsMode = v.Equals("Gps", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception e)
        {
            RLog.Warning($"Map config load failed: {e.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(_configPath, $"Mode={(_gpsMode ? "Gps" : "Map")}\n");
        }
        catch (Exception e)
        {
            RLog.Warning($"Map config save failed: {e.Message}");
        }
    }

    private static void SetOpen(bool open, bool restoreCursor = true)
    {
        if (open)
        {
            if (!EnsureAssets())
            {
                SonsTools.ShowMessage("Full map is not ready yet");
                return;
            }
            EnsureUi();
            _zoom = MaxZoom;
            _nextScan = 0f;
        }
        if (_open != open)
        {
            BlockInput(open);
            if (!open && restoreCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
        _open = open;
        if (_canvas) _canvas.gameObject.SetActive(open);
    }

    private static void BlockInput(bool block)
    {
        try
        {
            if (block)
            {
                Blocked.Clear();
                var actions = InputSystem.ListEnabledActions();
                for (var i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a != null && a.actionMap != null && a.actionMap.name == GpsActionMap && BlockedActions.Contains(a.name))
                        Blocked.Add(a);
                }
                foreach (var a in Blocked) a.Disable();
            }
            else
            {
                foreach (var a in Blocked)
                    if (a != null && a.actionMap != null && a.actionMap.enabled) a.Enable();
                Blocked.Clear();
            }
        }
        catch (Exception e)
        {
            if (_warnedBlock) return;
            _warnedBlock = true;
            RLog.Warning($"Map could not block mouse look: {e.Message}");
        }
    }

    private static void HandleClick()
    {
        var mouse = Mouse.current;
        if (mouse == null || !_view || !_canvas) return;
        var left = mouse.leftButton.wasPressedThisFrame;
        var right = mouse.rightButton.wasPressedThisFrame;
        if (!left && !right) return;
        if (right)
        {
            ClearMine();
            return;
        }

        var pos = mouse.position.ReadValue();
        var scale = _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        var local = (pos - new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)) / scale;
        var side = _view.rect.width;
        if (Mathf.Abs(local.x) > side * 0.5f || Mathf.Abs(local.y) > side * 0.5f) return;

        if (Waypoints.TryGetValue(SelfKey, out var mine) && mine.MapRect && mine.MapRect.gameObject.activeSelf &&
            (mine.MapRect.anchoredPosition - local).magnitude < WaypointSize * 0.7f)
        {
            ClearMine();
            return;
        }

        var uv = new Vector2(_lastView.x + (local.x / side + 0.5f) * _lastView.width, _lastView.y + (local.y / side + 0.5f) * _lastView.height);
        var xz = new Vector2((uv.x - 0.5f) * _worldSize.x * 2f, (uv.y - 0.5f) * _worldSize.y * 2f);
        SetWaypoint(SelfKey, xz, YouColor);
        Broadcast($"{ChatTag}{MyName()} set a waypoint at {Mathf.RoundToInt(xz.x).ToString(CultureInfo.InvariantCulture)}, {Mathf.RoundToInt(xz.y).ToString(CultureInfo.InvariantCulture)}");
    }

    private static void ClearMine()
    {
        if (!Waypoints.ContainsKey(SelfKey)) return;
        RemoveWaypoint(SelfKey);
        Broadcast($"{ChatTag}{MyName()} cleared the waypoint");
    }

    private static string MyName()
    {
        var local = LocalPlayer.Entity;
        var name = local != null ? GetName(local) : string.Empty;
        return string.IsNullOrEmpty(name) ? "Someone" : name;
    }

    private static Color ColorFor(string name)
    {
        if (string.IsNullOrEmpty(name)) return OtherColor;
        var h = 17;
        foreach (var ch in name.ToLowerInvariant()) h = unchecked(h * 31 + ch);
        return Palette[(h & 0x7fffffff) % Palette.Length];
    }

    private static void SetWaypoint(string key, Vector2 xz, Color color)
    {
        if (!Waypoints.TryGetValue(key, out var w))
        {
            w = new Waypoint { Owner = key };
            Waypoints[key] = w;
        }
        w.Xz = xz;
        w.Color = color;
        w.Expires = Time.unscaledTime + WaypointLifetime;
    }

    private static void RemoveWaypoint(string key)
    {
        if (!Waypoints.TryGetValue(key, out var w)) return;
        if (w.MapRect) UnityEngine.Object.Destroy(w.MapRect.gameObject);
        if (w.WorldRect) UnityEngine.Object.Destroy(w.WorldRect.gameObject);
        Waypoints.Remove(key);
    }

    private static void FindChat()
    {
        if (_chat || Time.unscaledTime < _nextChatLookup) return;
        _nextChatLookup = Time.unscaledTime + 2f;
        foreach (var c in Resources.FindObjectsOfTypeAll<ChatBox>())
        {
            if (!c || !c.gameObject.scene.isLoaded) continue;
            _chat = c;
            SeenRows.Clear();
            break;
        }
    }

    private static void PollChat()
    {
        if (Time.unscaledTime < _nextChatPoll) return;
        _nextChatPoll = Time.unscaledTime + 0.2f;
        try
        {
            if (!BoltNetwork.isRunning) return;
            FindChat();
            if (!_chat) return;
            var holder = _chat._messages;
            if (!holder) return;
            var parent = holder.transform;
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (!child || !child.gameObject.activeSelf) continue;
                var row = child.GetComponent<ChatMessageRow>();
                if (!row) continue;
                var label = row._message;
                if (!label) continue;
                var text = label.text;
                if (string.IsNullOrEmpty(text)) continue;
                if (SeenRows.TryGetValue(child.Pointer, out var seen) && seen == text) continue;
                SeenRows[child.Pointer] = text;
                var at = text.IndexOf(ChatTag, StringComparison.Ordinal);
                if (at < 0) continue;
                child.gameObject.SetActive(false);
                HandleChat(text.Substring(at + ChatTag.Length).Trim());
            }
            if (SeenRows.Count > 200) SeenRows.Clear();
        }
        catch (Exception e)
        {
            if (_warnedChat) return;
            _warnedChat = true;
            RLog.Warning($"Map could not read chat: {e.Message}");
        }
    }

    private static void Broadcast(string line)
    {
        try
        {
            if (!BoltNetwork.isRunning) return;
            _nextChatLookup = 0f;
            FindChat();
            if (!_chat)
            {
                if (!_warnedChat)
                {
                    _warnedChat = true;
                    RLog.Warning("Map could not find the chat box to share the waypoint");
                }
                return;
            }
            _chat.SendLine(line);
        }
        catch (Exception e)
        {
            RLog.Warning($"Map could not send the waypoint: {e.Message}");
        }
    }

    private static void HandleChat(string body)
    {
        var me = MyName();
        var set = SetRx.Match(body);
        if (set.Success)
        {
            var name = set.Groups[1].Value.Trim();
            if (name == me) return;
            if (!int.TryParse(set.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) return;
            if (!int.TryParse(set.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)) return;
            SetWaypoint(name, new Vector2(x, z), ColorFor(name));
            return;
        }

        var clear = ClearRx.Match(body);
        if (clear.Success)
        {
            var name = clear.Groups[1].Value.Trim();
            if (name != me) RemoveWaypoint(name);
        }
    }

    private static void UpdateWaypoints()
    {
        if (Waypoints.Count == 0)
        {
            if (_worldCanvas && _worldCanvas.enabled) _worldCanvas.enabled = false;
            return;
        }

        var now = Time.unscaledTime;
        foreach (var key in Waypoints.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
            RemoveWaypoint(key);

        var player = LocalPlayer.Transform;
        var cam = LocalPlayer.MainCam ? LocalPlayer.MainCam : Camera.main;
        if (Waypoints.Count == 0 || !player || !cam)
        {
            if (_worldCanvas && _worldCanvas.enabled) _worldCanvas.enabled = false;
            return;
        }

        if (!_mapTex) EnsureAssets();
        EnsureWorldCanvas();
        if (!_worldCanvas.enabled) _worldCanvas.enabled = true;

        var p = player.position;
        var scale = Screen.height / 1080f;
        foreach (var w in Waypoints.Values)
        {
            if (!w.WorldRect) CreateWorldMarker(w);
            var flat = new Vector2(w.Xz.x - p.x, w.Xz.y - p.z).magnitude;
            var world = new Vector3(w.Xz.x, GroundY(w.Xz, p.y) + 3f, w.Xz.y);
            var sp = cam.WorldToScreenPoint(world);
            var alpha = Mathf.InverseLerp(5f, 10f, flat);
            if (sp.z <= 0f || alpha <= 0.01f)
            {
                if (w.WorldRect.gameObject.activeSelf) w.WorldRect.gameObject.SetActive(false);
                continue;
            }
            if (!w.WorldRect.gameObject.activeSelf) w.WorldRect.gameObject.SetActive(true);
            var size = Mathf.Lerp(36f, 22f, Mathf.InverseLerp(0f, 1500f, flat)) * scale;
            w.WorldGroup.alpha = alpha;
            w.WorldRect.sizeDelta = new Vector2(size, size);
            w.WorldRect.position = new Vector3(sp.x, sp.y + size * 0.5f, 0f);
            if (w.Distance)
            {
                w.Distance.fontSize = Mathf.RoundToInt(16f * scale);
                w.Distance.text = flat >= 1000f ? $"{flat / 1000f:F1} km" : $"{flat:F0} m";
            }
        }
    }

    private static float GroundY(Vector2 xz, float fallback)
    {
        var t = Terrain.activeTerrain;
        if (!t) return fallback;
        return t.SampleHeight(new Vector3(xz.x, 0f, xz.y)) + t.transform.position.y;
    }

    private static void EnsureWorldCanvas()
    {
        if (_worldCanvas) return;
        var go = new GameObject("MapWaypointCanvas");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _worldCanvas = go.AddComponent<Canvas>();
        _worldCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _worldCanvas.sortingOrder = -45;
    }

    private static void CreateWorldMarker(Waypoint w)
    {
        var go = new GameObject($"Waypoint_{w.Owner}");
        go.transform.SetParent(_worldCanvas.transform, false);
        var r = go.AddComponent<RectTransform>();
        var group = go.AddComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        BuildPin(go.transform, w.Color);
        if (_font)
        {
            var tgo = new GameObject("Distance");
            tgo.transform.SetParent(go.transform, false);
            var t = tgo.AddComponent<Text>();
            t.font = _font;
            t.alignment = TextAnchor.UpperCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.color = Color.white;
            t.raycastTarget = false;
            var outline = tgo.AddComponent<Outline>();
            outline.effectColor = Color.black;
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            var tr = t.rectTransform;
            tr.anchorMin = new Vector2(0.5f, 0f);
            tr.anchorMax = new Vector2(0.5f, 0f);
            tr.pivot = new Vector2(0.5f, 1f);
            tr.sizeDelta = new Vector2(200f, 24f);
            tr.anchoredPosition = new Vector2(0f, -4f);
            w.Distance = t;
        }
        go.SetActive(false);
        w.WorldRect = r;
        w.WorldGroup = group;
    }

    private static void CreateMapWaypoint(Waypoint w)
    {
        var go = new GameObject($"MapWaypoint_{w.Owner}");
        go.transform.SetParent(_view, false);
        var r = go.AddComponent<RectTransform>();
        Center(r, WaypointSize);
        BuildPin(go.transform, w.Color);
        go.transform.SetAsLastSibling();
        if (_player) _player.SetAsLastSibling();
        w.MapRect = r;
    }

    private static void BuildPin(Transform parent, Color color)
    {
        AddRaw(parent, "Ring", _circleTex, Color.black, 1.15f);
        AddRaw(parent, "Background", _circleTex, color, 1f);
        if (_waypointTex) AddRaw(parent, "Icon", _waypointTex, Color.black, 0.62f);
    }

    private static void HandleZoom()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;
        var y = mouse.scroll.y.ReadValue();
        if (Mathf.Abs(y) < 0.01f) return;
        _zoom = Mathf.Clamp(_zoom * Mathf.Pow(ZoomStep, Mathf.Sign(y)), 1f, MaxZoom);
    }

    private static bool EnsureAssets()
    {
        if (!_mapTex) FindMap();
        if ((!_kelvinTex || !_heartTex || !_circleTex || !_waypointTex || _waypointTex.name != WaypointIconName) && Time.unscaledTime >= _nextIconLookup)
        {
            _nextIconLookup = Time.unscaledTime + 10f;
            FindIcons();
        }
        return _mapTex;
    }

    private static void FindMap()
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<GPSTrackerSystem>())
        {
            if (!t) continue;
            var surface = t._surfaceMap;
            if (!surface) continue;
            var img = surface.GetComponent<Image>();
            if (!img) continue;
            var sprite = img.sprite;
            if (!sprite || !sprite.texture) continue;

            _mapTex = sprite.texture;
            var r = sprite.textureRect;
            _mapUv = new Rect(r.x / _mapTex.width, r.y / _mapTex.height, r.width / _mapTex.width, r.height / _mapTex.height);
            var ws = t._worldSize;
            if (ws.x > 1f && ws.y > 1f) _worldSize = ws;

            var arrow = t._playerArrow;
            if (arrow)
            {
                var ai = arrow.GetComponent<Image>();
                if (ai && ai.sprite) _arrowSprite = ai.sprite;
            }

            var day = t._dayText;
            if (day && day.font) _font = day.font;

            RLog.Msg($"Map using {_mapTex.name} {_mapTex.width}x{_mapTex.height}, map covers {_worldSize.x * 2f}x{_worldSize.y * 2f} m");
            return;
        }
    }

    private static void FindWaypointIcon()
    {
        try
        {
            foreach (var icons in Resources.FindObjectsOfTypeAll<GPSLocatorIcons>())
            {
                if (!icons) continue;
                var list = icons.IconDataList;
                if (list == null) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    object item = list[i];
                    if (item == null) continue;
                    foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!typeof(Texture).IsAssignableFrom(p.PropertyType)) continue;
                        Texture tex;
                        try { tex = p.GetValue(item) as Texture; }
                        catch { continue; }
                        if (!tex || tex.name != WaypointIconName) continue;
                        _waypointTex = tex;
                        return;
                    }
                }
            }
        }
        catch (Exception e)
        {
            RLog.Warning($"Map could not read the GPS icon list: {e.Message}");
        }
    }

    private static void FindIcons()
    {
        FindWaypointIcon();
        foreach (var ri in Resources.FindObjectsOfTypeAll<RawImage>())
        {
            if (!ri) continue;
            var t = ri.texture;
            if (!t) continue;
            switch (t.name)
            {
                case "RobbyLogo":
                    if (!_kelvinTex) _kelvinTex = t;
                    break;
                case "HeartIcon":
                    if (!_heartTex || _heartTex.name != "HeartIcon") _heartTex = t;
                    break;
                case "Heart":
                    if (!_heartTex) _heartTex = t;
                    break;
                case "Circle":
                    if (!_circleTex) _circleTex = t;
                    break;
                case WaypointIconName:
                    _waypointTex = t;
                    break;
            }
        }
        if (_canvas && _kelvinTex && _heartTex) ClearMarkers();
        foreach (var w in Waypoints.Values)
        {
            if (w.MapRect) UnityEngine.Object.Destroy(w.MapRect.gameObject);
            if (w.WorldRect) UnityEngine.Object.Destroy(w.WorldRect.gameObject);
            w.MapRect = null;
            w.WorldRect = null;
        }
    }

    private static void EnsureUi()
    {
        if (_canvas) return;

        var root = new GameObject("MapCanvas");
        UnityEngine.Object.DontDestroyOnLoad(root);
        _canvas = root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 200;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;

        var bg = new GameObject("Background");
        bg.transform.SetParent(root.transform, false);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, BackgroundAlpha);
        bgImg.raycastTarget = false;
        Stretch(bgImg.rectTransform, 1f);

        var view = new GameObject("View");
        view.transform.SetParent(root.transform, false);
        _view = view.AddComponent<RectTransform>();
        _view.anchorMin = new Vector2(0.5f, 0.5f);
        _view.anchorMax = new Vector2(0.5f, 0.5f);
        var side = 1080f * ViewFraction;
        _view.sizeDelta = new Vector2(side, side);
        view.AddComponent<RectMask2D>();

        var map = new GameObject("Map");
        map.transform.SetParent(view.transform, false);
        _map = map.AddComponent<RawImage>();
        _map.texture = _mapTex;
        _map.raycastTarget = false;
        Stretch(_map.rectTransform, 1f);

        _player = CreateArrow(_view, "You", ArrowSize, YouColor);
        Markers.Clear();
        root.SetActive(false);
    }

    private static RectTransform CreateArrow(Transform parent, string name, float size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var r = go.AddComponent<RectTransform>();
        Center(r, size);

        if (_arrowSprite)
        {
            AddImage(go.transform, "Outline", _arrowSprite, Color.black, 1f, Vector2.zero);
            AddImage(go.transform, "Inner", _arrowSprite, color, 0.66f, new Vector2(0f, -size * 10f / 128f));
        }
        else
        {
            AddRaw(go.transform, "Ring", _circleTex, Color.black, 1f);
            AddRaw(go.transform, "Dot", _circleTex, color, 0.7f);
        }
        return r;
    }

    private static Marker CreateMarker(Found f)
    {
        var go = new GameObject($"Marker_{f.Kind}");
        go.transform.SetParent(_view, false);
        var r = go.AddComponent<RectTransform>();
        var m = new Marker { Kind = f.Kind, Target = f.Target, Rect = r };

        if (f.Kind == Locator)
        {
            Center(r, LocatorSize);
            m.Signature = f.Signature;
            if (f.HasBackground)
            {
                AddRaw(go.transform, "Ring", _circleTex, Color.black, 1.15f);
                AddRaw(go.transform, "Background", _circleTex, f.BackgroundColor, 1f);
                if (f.Icon) AddRaw(go.transform, "Icon", f.Icon, f.IconColor, 0.72f);
            }
            else
            {
                if (f.Outline) AddRaw(go.transform, "Outline", f.Outline, f.OutlineColor, 1f);
                if (f.Icon) AddRaw(go.transform, "Icon", f.Icon, f.IconColor, 1f);
            }
            go.transform.SetSiblingIndex(1);
            return m;
        }

        if (f.Kind == Player)
        {
            Center(r, OtherArrowSize);
            m.Arrow = CreateArrow(go.transform, "Arrow", OtherArrowSize, ColorFor(f.Name));
            m.Label = CreateLabel(go.transform, f.Name, OtherArrowSize);
        }
        else
        {
            Center(r, MarkerSize);
            var bg = f.Kind == Kelvin ? new Color(0f, 0f, 1f, 1f) : new Color(0.973f, 0.667f, 1f, 1f);
            var icon = f.Kind == Kelvin ? _kelvinTex : _heartTex;
            AddRaw(go.transform, "Ring", _circleTex, Color.black, 1.15f);
            AddRaw(go.transform, "Background", _circleTex, bg, 1f);
            if (icon) AddRaw(go.transform, "Icon", icon, Color.white, 0.72f);
        }

        if (_player) _player.SetAsLastSibling();
        return m;
    }

    private static Text CreateLabel(Transform parent, string text, float markerSize)
    {
        if (!_font) return null;
        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = _font;
        t.fontSize = LabelFontSize;
        t.alignment = TextAnchor.LowerCenter;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.color = Color.white;
        t.raycastTarget = false;
        t.text = text;
        var outline = go.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(1.5f, -1.5f);
        var r = t.rectTransform;
        r.anchorMin = new Vector2(0.5f, 0.5f);
        r.anchorMax = new Vector2(0.5f, 0.5f);
        r.sizeDelta = new Vector2(240f, 24f);
        r.anchoredPosition = new Vector2(0f, markerSize * 0.5f + 14f);
        return t;
    }

    private static void AddRaw(Transform parent, string name, Texture tex, Color color, float scale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<RawImage>();
        img.texture = tex;
        img.color = color;
        img.raycastTarget = false;
        Stretch(img.rectTransform, scale);
    }

    private static void AddImage(Transform parent, string name, Sprite sprite, Color color, float scale, Vector2 offset)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = false;
        Stretch(img.rectTransform, scale);
        img.rectTransform.anchoredPosition = offset;
    }

    private static void Stretch(RectTransform r, float scale)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = Vector2.zero;
        r.offsetMax = Vector2.zero;
        r.localScale = new Vector3(scale, scale, 1f);
    }

    private static void Center(RectTransform r, float size)
    {
        r.anchorMin = new Vector2(0.5f, 0.5f);
        r.anchorMax = new Vector2(0.5f, 0.5f);
        r.sizeDelta = new Vector2(size, size);
    }

    private static void Layout()
    {
        var player = LocalPlayer.Transform;
        if (!player || !_view) return;

        var side = _view.rect.width;
        var w = 1f / _zoom;
        var p = WorldToUv(player.position);
        var cx = Mathf.Clamp(p.x, w * 0.5f, 1f - w * 0.5f);
        var cy = Mathf.Clamp(p.y, w * 0.5f, 1f - w * 0.5f);
        var view = new Rect(cx - w * 0.5f, cy - w * 0.5f, w, w);
        _lastView = view;

        _map.uvRect = new Rect(
            _mapUv.x + view.x * _mapUv.width,
            _mapUv.y + view.y * _mapUv.height,
            view.width * _mapUv.width,
            view.height * _mapUv.height);

        Place(_player, p, view, side);
        _player.localEulerAngles = new Vector3(0f, 0f, -player.eulerAngles.y);

        foreach (var m in Markers)
        {
            if (!m.Rect) continue;
            if (!m.Target || !m.Target.gameObject.activeInHierarchy)
            {
                m.Rect.gameObject.SetActive(false);
                continue;
            }
            Place(m.Rect, WorldToUv(m.Target.position), view, side);
            if (m.Arrow) m.Arrow.localEulerAngles = new Vector3(0f, 0f, -m.Target.eulerAngles.y);
        }

        foreach (var wp in Waypoints.Values)
        {
            if (!wp.MapRect) CreateMapWaypoint(wp);
            Place(wp.MapRect, new Vector2(wp.Xz.x / (_worldSize.x * 2f) + 0.5f, wp.Xz.y / (_worldSize.y * 2f) + 0.5f), view, side);
        }
    }

    private static Vector2 WorldToUv(Vector3 world)
    {
        return new Vector2(world.x / (_worldSize.x * 2f) + 0.5f, world.z / (_worldSize.y * 2f) + 0.5f);
    }

    private static void Place(RectTransform r, Vector2 uv, Rect view, float side)
    {
        var x = (uv.x - view.x) / view.width;
        var y = (uv.y - view.y) / view.height;
        var inside = x >= 0f && x <= 1f && y >= 0f && y <= 1f;
        if (r.gameObject.activeSelf != inside) r.gameObject.SetActive(inside);
        r.anchoredPosition = new Vector2((x - 0.5f) * side, (y - 0.5f) * side);
    }

    private static void Rescan()
    {
        var found = new List<Found>();
        CollectCompanions(found);
        CollectPlayers(found);
        CollectLocators(found);

        for (var i = Markers.Count - 1; i >= 0; i--)
        {
            var m = Markers[i];
            if (m.Rect && m.Target && found.Any(f => f.Target.Pointer == m.Target.Pointer && f.Kind == m.Kind && f.Signature == m.Signature)) continue;
            if (m.Rect) UnityEngine.Object.Destroy(m.Rect.gameObject);
            Markers.RemoveAt(i);
        }

        foreach (var f in found)
        {
            var existing = Markers.FirstOrDefault(m => m.Target.Pointer == f.Target.Pointer && m.Kind == f.Kind);
            if (existing == null)
            {
                try
                {
                    Markers.Add(CreateMarker(f));
                }
                catch (Exception e)
                {
                    if (WarnedCreate.Add(f.Kind))
                        RLog.Error($"Map could not create the {f.Kind} marker: {e}");
                }
                continue;
            }
            if (existing.Label && existing.Label.text != f.Name)
                existing.Label.text = f.Name;
        }

        var summary = $"players {found.Count(f => f.Kind == Player)}, Kelvin {found.Count(f => f.Kind == Kelvin)}, Virginia {found.Count(f => f.Kind == Virginia)}, GPS icons {found.Count(f => f.Kind == Locator)}, markers {Markers.Count}";
        if (summary != _lastSummary)
        {
            _lastSummary = summary;
            RLog.Msg($"Map found {summary}");
        }
    }

    private static void CollectPlayers(List<Found> found)
    {
        try
        {
            if (!BoltNetwork.isRunning) return;
            var tracker = PlayerTracker.Instance;
            if (tracker == null || tracker.AllPlayerEntities == null) return;
            var local = LocalPlayer.Entity;
            var localTransform = LocalPlayer.Transform;
            var localRoot = localTransform ? localTransform.root : null;

            foreach (var entity in tracker.AllPlayerEntities)
            {
                if (entity == null || !entity.gameObject) continue;
                if (local != null && entity.Pointer == local.Pointer) continue;
                var target = entity.transform;
                if (!target || found.Any(f => f.Target.Pointer == target.Pointer)) continue;
                if (localTransform && (target.Pointer == localTransform.Pointer || target.root.Pointer == localRoot.Pointer)) continue;
                if (IsSelf(entity, localTransform)) continue;
                var playerName = GetName(entity);
                found.Add(new Found { Kind = Player, Target = target, Name = playerName, Signature = playerName });
            }
        }
        catch (Exception e)
        {
            if (_warnedPlayers) return;
            _warnedPlayers = true;
            RLog.Warning($"Map could not read players: {e.Message}");
        }
    }

    private static void CollectLocators(List<Found> found)
    {
        try
        {
            GPSTrackerSystem source = null;
            var best = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<GPSTrackerSystem>())
            {
                if (!t) continue;
                var list = t._gpsLocators;
                var visuals = t._gpsLocatorVisuals;
                if (list == null || visuals == null) continue;
                var count = Math.Min(list.Count, visuals.Count);
                if (count <= best) continue;
                best = count;
                source = t;
            }
            if (!source) return;

            var locators = source._gpsLocators;
            var visualsList = source._gpsLocatorVisuals;
            var n = Math.Min(locators.Count, visualsList.Count);
            for (var i = 0; i < n; i++)
            {
                var locator = locators[i];
                var visual = visualsList[i];
                if (!locator || !visual || !visual.gameObject.activeSelf) continue;
                var target = locator.transform;
                if (!target || found.Any(f => f.Target.Pointer == target.Pointer)) continue;

                var f = new Found { Kind = Locator, Target = target, Name = Locator };
                var iconOnly = visual.Find("IconOnly");
                if (iconOnly && iconOnly.gameObject.activeSelf)
                {
                    ReadIcon(iconOnly.Find("Icon"), ref f);
                }
                else
                {
                    ReadIcon(visual.Find("Pin/Icon"), ref f);
                    var bkg = visual.Find("Pin/IconBkg");
                    var bkgImg = bkg ? bkg.GetComponent<Image>() : null;
                    if (bkgImg)
                    {
                        f.HasBackground = true;
                        f.BackgroundColor = bkgImg.color;
                    }
                }

                if (!f.Icon) continue;
                if (f.Icon.name == "RobbyLogo") continue;
                f.Signature = $"{f.Icon.name}|{f.IconColor}|{f.HasBackground}|{f.BackgroundColor}|{(f.Outline ? f.Outline.name : "")}";
                found.Add(f);
            }
        }
        catch (Exception e)
        {
            if (_warnedLocators) return;
            _warnedLocators = true;
            RLog.Warning($"Map could not read GPS icons: {e}");
        }
    }

    private static void ReadIcon(Transform icon, ref Found f)
    {
        if (!icon) return;
        var raw = icon.GetComponent<RawImage>();
        if (!raw || !raw.texture) return;
        f.Icon = raw.texture;
        f.IconColor = raw.color;
        var outline = icon.Find("IconOutline");
        if (!outline || !outline.gameObject.activeSelf) return;
        var oraw = outline.GetComponent<RawImage>();
        if (!oraw || !oraw.texture) return;
        f.Outline = oraw.texture;
        f.OutlineColor = oraw.color;
    }

    private static bool IsSelf(BoltEntity entity, Transform localTransform)
    {
        try
        {
            if (entity.isAttached && entity.hasControl) return true;
        }
        catch
        {
        }
        return localTransform && (entity.transform.position - localTransform.position).sqrMagnitude < 4f;
    }

    private static string GetName(BoltEntity entity)
    {
        try
        {
            if (entity.TryFindState<IPlayerState>(out var state) && !string.IsNullOrWhiteSpace(state.name) && !state.name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return state.name;
        }
        catch
        {
        }
        return string.Empty;
    }

    private static void CollectCompanions(List<Found> found)
    {
        try
        {
            var robby = ActorTools.GetRobby();
            if (robby) Add(found, Kelvin, robby.transform);
        }
        catch (Exception e)
        {
            RLog.Warning($"GetRobby: {e.Message}");
        }

        try
        {
            var virginias = ActorTools.GetActors(VailActorTypeId.Virginia);
            if (virginias != null)
            {
                foreach (var v in virginias)
                    if (v) Add(found, Virginia, v.transform);
            }
        }
        catch (Exception e)
        {
            RLog.Warning($"GetActors Virginia: {e.Message}");
        }

        var needKelvin = !found.Any(f => f.Kind == Kelvin);
        var needVirginia = !found.Any(f => f.Kind == Virginia);
        if (!needKelvin && !needVirginia) return;

        var roots = new HashSet<IntPtr>();
        foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
        {
            if (!a) continue;
            var root = a.transform.root;
            if (!roots.Add(root.Pointer)) continue;
            var name = root.name;
            if (name.Contains("Creepy")) continue;
            if (needKelvin && name.StartsWith("Robby"))
                Add(found, Kelvin, root);
            else if (needVirginia && name.StartsWith("Virginia"))
                Add(found, Virginia, root);
        }
    }

    private static void Add(List<Found> found, string kind, Transform target)
    {
        if (target && !found.Any(f => f.Target.Pointer == target.Pointer))
            found.Add(new Found { Kind = kind, Target = target, Name = kind });
    }

    private static void ClearMarkers()
    {
        foreach (var m in Markers)
            if (m.Rect) UnityEngine.Object.Destroy(m.Rect.gameObject);
        Markers.Clear();
    }
}

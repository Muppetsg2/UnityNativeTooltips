using System;
using System.Collections.Generic;
using NativeTooltips.Backends;
using UnityEngine;
#if NATIVE_TOOLTIPS_UGUI
using UnityEngine.EventSystems;
#endif

namespace NativeTooltips.Internal
{
    /// <summary>
    /// Optional: sources nested in a hierarchy (UGUI, UI Toolkit) report their depth so that the
    /// deepest element wins over a parent the cursor is technically also over.
    /// </summary>
    public interface IHierarchicalTooltipSource
    {
        int HierarchyDepth { get; }
    }

    /// <summary>
    /// Hidden driver object: the state machine (hover delay, long-press, auto-hide) shared by all
    /// platforms. A backend only shows/hides the bubble.
    /// </summary>
    [AddComponentMenu("")]
    [DefaultExecutionOrder(-500)]
    internal sealed class TooltipDriver : MonoBehaviour
    {
        enum Phase
        {
            Idle,
            HoverWaiting,
            LongPressWaiting,
            Visible,
            Suppressed
        }

        sealed class ManualSource : IRichTooltipSource
        {
            public string Text;
            public string Title;
            public TooltipIcon Icon;
            public TooltipStyle Style;
            public Rect? ScreenRect;
            public string TooltipText => Text;
            public string TooltipTitle => Title;
            public TooltipIcon TooltipIcon => Icon;
            public TooltipStyle TooltipStyle => Style;
            public bool IsTooltipActive => true;

            public bool TryGetScreenRect(out Rect rect)
            {
                rect = ScreenRect ?? default;
                return ScreenRect.HasValue;
            }
        }

        static TooltipDriver s_Instance;
        static bool s_Quitting;

        ITooltipBackend _backend;
        Phase _phase;
        ITooltipSource _source;
        TooltipTrigger _trigger;
        float _deadline;
        float _waitDuration;
        float _hideAt;
        Vector2 _pointer;
        Vector2 _restPosition;
        Vector2 _pressPosition;
        Action _onLongPress;
        bool _touchDown;
        bool _suppressedBecauseEmpty;
        float _lastVisibleEnd = -999f;
        string _shownText;
        string _shownTitle;
        ITooltipSource _clickSuppressed; // clicked with the mouse — no hover tooltip until the pointer leaves it
        readonly ManualSource _manual = new ManualSource();
        readonly List<ITooltipSource> _hovered = new List<ITooltipSource>(4);

        static float Now => Time.unscaledTime;

        public static bool HasInstance => s_Instance != null;

        public static TooltipDriver Instance
        {
            get
            {
                if (s_Instance != null) return s_Instance;

                var go = new GameObject("[NativeTooltips]") { hideFlags = HideFlags.HideAndDontSave };
                if (Application.isPlaying) DontDestroyOnLoad(go);
                s_Instance = go.AddComponent<TooltipDriver>();
                if (s_Instance._backend == null) s_Instance.RecreateBackend(); // when Awake did not run (edit mode)
                return s_Instance;
            }
        }

        public ITooltipBackend Backend => _backend;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Support "Enter Play Mode Options" with domain reload disabled.
            s_Instance = null;
            s_Quitting = false;
        }

        void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_Instance = this;
            RecreateBackend();
        }

        public void RecreateBackend()
        {
            ResetState();
            if (_backend != null)
            {
                try { _backend.Shutdown(); }
                catch (Exception e) { Debug.LogException(e); }
            }

            _backend = TooltipSettings.Enabled ? BackendFactory.Create() : new NullTooltipBackend();
        }

        // ------------------------------------------------------------------ pointer events

        public void PointerEnter(ITooltipSource source, Vector2 screenPosition, bool isTouch)
        {
            if (source == null || !TooltipSettings.Enabled) return;
            _pointer = screenPosition;
            if (isTouch) return; // touch has no hover — PointerDown handles it (long-press)

            if (!_hovered.Contains(source)) _hovered.Add(source);
            ITooltipSource best = DeepestHovered();
            if (!ReferenceEquals(best, source)) return; // parent of the current source — waits on the stack
            if (ReferenceEquals(source, _source) && _phase != Phase.Idle) return;
            if (_trigger == TooltipTrigger.Manual && _phase == Phase.Visible) return; // resumed when it ends
            if (ReferenceEquals(source, _clickSuppressed)) return;

            BeginHover(source, screenPosition);
        }

        public void PointerMove(ITooltipSource source, Vector2 screenPosition, bool isTouch)
        {
            _pointer = screenPosition;
            if (!ReferenceEquals(source, _source)) return;

            if (isTouch)
            {
                if (_phase == Phase.LongPressWaiting &&
                    (screenPosition - _pressPosition).sqrMagnitude > DragThreshold * DragThreshold)
                {
                    ResetState(); // the finger moved — it's a scroll/drag, not a long-press
                }

                return;
            }

            if (_phase == Phase.HoverWaiting && TooltipSettings.RequireStationaryPointer)
            {
                float tol = TooltipSettings.StationaryTolerance;
                if ((screenPosition - _restPosition).sqrMagnitude > tol * tol)
                {
                    _restPosition = screenPosition;
                    _deadline = Now + _waitDuration;
                }
            }
        }

        public void PointerExit(ITooltipSource source, bool isTouch)
        {
            _hovered.Remove(source);
            if (ReferenceEquals(source, _clickSuppressed)) _clickSuppressed = null;
            if (!ReferenceEquals(source, _source)) return;

            if (_trigger == TooltipTrigger.LongPress)
            {
                if (_phase == Phase.Visible)
                {
                    // UGUI sends "exit" when the finger is lifted — keep the bubble for a moment.
                    _hideAt = Now + TooltipSettings.TouchHideDelay;
                    return;
                }

                ResetState();
                return;
            }

            if (_trigger == TooltipTrigger.Manual) return;

            bool wasVisible = _phase == Phase.Visible;
            ResetState();

            if (!isTouch)
            {
                ITooltipSource parent = DeepestHovered();
                if (parent != null) BeginHover(parent, _pointer, forceReshow: wasVisible);
            }
        }

        public void PointerDown(ITooltipSource source, Vector2 screenPosition, bool isTouch, Action onLongPress)
        {
            if (source == null || !TooltipSettings.Enabled) return;
            _pointer = screenPosition;

            if (!isTouch)
            {
                // A click hides the bubble until the pointer leaves the element (like Win32/AppKit/GTK).
                _clickSuppressed = source;
                if (_trigger != TooltipTrigger.Manual && _source != null)
                {
                    HideVisible();
                    _phase = Phase.Suppressed;
                }

                return;
            }

            ResetState(); // every new touch hides the previous bubble
            _source = source;
            _trigger = TooltipTrigger.LongPress;
            _phase = Phase.LongPressWaiting;
            _deadline = Now + LongPressTimeout;
            _pressPosition = screenPosition;
            _onLongPress = onLongPress;
            _touchDown = true;
        }

        public void PointerUp(ITooltipSource source, bool isTouch)
        {
            if (!isTouch || !ReferenceEquals(source, _source)) return;

            _touchDown = false;
            if (_phase == Phase.LongPressWaiting)
            {
                ResetState(); // a plain tap
                return;
            }

            if (_phase == Phase.Visible && _trigger == TooltipTrigger.LongPress)
                _hideAt = Now + TooltipSettings.TouchHideDelay;
        }

        public void Release(ITooltipSource source)
        {
            _hovered.Remove(source);
            if (ReferenceEquals(source, _clickSuppressed)) _clickSuppressed = null;
            if (ReferenceEquals(source, _source)) ResetState();
        }

        // ------------------------------------------------------------------ manual API

        public void ShowManual(string text, Rect? screenRect, float duration,
            TooltipStyle style = null, string title = null, TooltipIcon icon = TooltipIcon.None)
        {
            if (!TooltipSettings.Enabled) return;
            ResetState();
            if (string.IsNullOrEmpty(text)) return;

            _manual.Text = text;
            _manual.Title = title;
            _manual.Icon = icon;
            _manual.Style = style;
            _manual.ScreenRect = screenRect;
            _source = _manual;
            _trigger = TooltipTrigger.Manual;
            ShowNow();
            if (_phase == Phase.Visible && duration > 0f) _hideAt = Now + duration;
        }

        public void HideAll()
        {
            if (_source != null && _trigger == TooltipTrigger.Hover)
            {
                HideVisible();
                _phase = Phase.Suppressed; // don't show again until the pointer leaves the element
                return;
            }

            bool wasManual = _source != null && _trigger == TooltipTrigger.Manual;
            ResetState();
            if (wasManual) ResumeHover();
        }

        /// <summary>
        /// A manual tooltip just ended: hovers that happened meanwhile were held back, so hand over to the
        /// element under the pointer (after the short "reshow" delay) instead of waiting for a new enter.
        /// </summary>
        void ResumeHover()
        {
            ITooltipSource next = DeepestHovered();
            if (next != null && !ReferenceEquals(next, _clickSuppressed))
                BeginHover(next, _pointer, forceReshow: true);
        }

        // ------------------------------------------------------------------ loop

        void Update()
        {
            if (_backend == null) return;

            try { _backend.Tick(); }
            catch (Exception e) { FailBackend(e); return; }

            if (_source == null) return;

            if (!TooltipSettings.Enabled || !IsAlive(_source))
            {
                _hovered.Remove(_source);
                ResetState();
                return;
            }

            float now = Now;
            switch (_phase)
            {
                case Phase.HoverWaiting:
                    if (now >= _deadline) ShowNow();
                    break;

                case Phase.LongPressWaiting:
                    if (now >= _deadline)
                    {
                        ShowNow();
                        if (_phase == Phase.Visible)
                        {
                            try { _onLongPress?.Invoke(); }
                            catch (Exception e) { Debug.LogException(e); }

                            // While the finger stays down the bubble stays (with a safety limit).
                            _hideAt = now + (_touchDown ? 30f : TooltipSettings.TouchHideDelay);
                        }
                    }

                    break;

                case Phase.Visible:
                    if (_hideAt > 0f && now >= _hideAt)
                    {
                        HideVisible();
                        if (_trigger == TooltipTrigger.Hover)
                        {
                            _phase = Phase.Suppressed; // auto-pop: until the pointer re-enters
                        }
                        else
                        {
                            bool wasManual = _trigger == TooltipTrigger.Manual;
                            ResetState();
                            if (wasManual) ResumeHover();
                        }

                        break;
                    }

                    string text = SafeText(_source);
                    if (string.IsNullOrEmpty(text))
                    {
                        HideVisible();
                        _phase = Phase.Suppressed;
                        _suppressedBecauseEmpty = true;
                    }
                    else if (!string.Equals(text, _shownText, StringComparison.Ordinal) ||
                             !string.Equals(SafeTitle(_source), _shownTitle, StringComparison.Ordinal))
                    {
                        float keepHideAt = _hideAt;
                        ShowNow(isUpdate: true); // live text update (e.g. localization, counters)
                        if (keepHideAt > 0f) _hideAt = keepHideAt;
                    }

                    break;

                case Phase.Suppressed:
                    if (_suppressedBecauseEmpty && !string.IsNullOrEmpty(SafeText(_source)))
                        ShowNow();
                    break;
            }
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus || _phase != Phase.Visible) return;

            if (_trigger == TooltipTrigger.Hover)
            {
                HideVisible();
                _phase = Phase.Suppressed;
            }
            else
            {
                ResetState();
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) ResetState();
        }

        void OnApplicationQuit()
        {
            s_Quitting = true;
            Shutdown();
        }

        void OnDestroy()
        {
            if (s_Instance != this) return;
            Shutdown();
            s_Instance = null;
        }

        void Shutdown()
        {
            ResetState();
            if (_backend == null) return;

            try { _backend.Shutdown(); }
            catch (Exception e) { if (!s_Quitting) Debug.LogException(e); }

            _backend = null;
        }

        // ------------------------------------------------------------------ internals

        void BeginHover(ITooltipSource source, Vector2 screenPosition, bool forceReshow = false)
        {
            bool recentlyVisible = forceReshow || _phase == Phase.Visible ||
                                   Now - _lastVisibleEnd < 0.5f;
            ResetState();

            _source = source;
            _trigger = TooltipTrigger.Hover;
            _pointer = screenPosition;
            _restPosition = screenPosition;

            TooltipRequest probe = BuildRequest(false);
            if (_backend.UsesSystemHoverDelay(probe))
            {
                ShowNow(); // the OS waits and shows the bubble itself
                return;
            }

            float delay = probe.Style.ShowDelay >= 0f ? probe.Style.ShowDelay : InitialDelay;
            _phase = Phase.HoverWaiting;
            _waitDuration = recentlyVisible ? Mathf.Min(_backend.ReshowDelay, delay) : delay;
            _deadline = Now + _waitDuration;
        }

        void ShowNow(bool isUpdate = false)
        {
            string text = SafeText(_source);
            if (string.IsNullOrEmpty(text))
            {
                _phase = _trigger == TooltipTrigger.Hover ? Phase.Suppressed : Phase.Idle;
                _suppressedBecauseEmpty = _phase == Phase.Suppressed;
                if (_phase == Phase.Idle) _source = null;
                return;
            }

            TooltipRequest request = BuildRequest(isUpdate);
            if (string.IsNullOrEmpty(request.Text))
            {
                // only tags, no visible text
                _phase = _trigger == TooltipTrigger.Hover ? Phase.Suppressed : Phase.Idle;
                _suppressedBecauseEmpty = _phase == Phase.Suppressed;
                if (_phase == Phase.Idle) _source = null;
                return;
            }

            bool systemHover = _trigger == TooltipTrigger.Hover && _backend.UsesSystemHoverDelay(request);

            try
            {
                _backend.Show(request);
            }
            catch (Exception e)
            {
                FailBackend(e);
                return;
            }

            _phase = Phase.Visible;
            _shownText = text;
            _shownTitle = SafeTitle(_source);
            _suppressedBecauseEmpty = false;
            _hideAt = 0f;

            if (_trigger == TooltipTrigger.Hover && !systemHover)
            {
                float autoPop = AutoPopDelay;
                if (autoPop > 0f) _hideAt = Now + autoPop;
            }
        }

        void HideVisible()
        {
            if (_phase == Phase.Visible)
            {
                try { _backend?.Hide(); }
                catch (Exception e) { Debug.LogException(e); }

                _lastVisibleEnd = Now;
            }

            _shownText = null;
            _shownTitle = null;
        }

        TooltipRequest BuildRequest(bool isUpdate)
        {
            var request = new TooltipRequest
            {
                Text = SafeText(_source),
                Trigger = _trigger,
                Pointer = ScreenRectUtility.ToNormalizedTopLeft(_pointer),
                IsUpdate = isUpdate,
                Style = TooltipSettings.Style
            };

            if (_source is IRichTooltipSource rich)
            {
                try
                {
                    request.Title = rich.TooltipTitle;
                    request.Icon = rich.TooltipIcon;
                    request.Style = rich.TooltipStyle ?? TooltipSettings.Style;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            // Rich text: plain Text/Title for backends that can't format + runs for those that can.
            // Text without recognised tags keeps runs == null (fully native path).
            if (TooltipSettings.RichText)
            {
                if (TooltipRichText.TryParse(request.Text, out var textRuns, out string plainText))
                {
                    request.Text = plainText;
                    request.TextRuns = textRuns;
                }

                if (TooltipRichText.TryParse(request.Title, out var titleRuns, out string plainTitle))
                {
                    request.Title = plainTitle;
                    request.TitleRuns = titleRuns;
                }
            }

            if (_source != null && SafeRect(_source, out Rect screenRect))
            {
                request.Anchor = ScreenRectUtility.ToNormalizedTopLeft(screenRect);
                request.HasAnchor = true;
            }

            return request;
        }

        static string SafeTitle(ITooltipSource s)
        {
            if (!(s is IRichTooltipSource rich)) return null;
            try { return rich.TooltipTitle; }
            catch { return null; }
        }

        void ResetState()
        {
            HideVisible();
            _phase = Phase.Idle;
            _source = null;
            _onLongPress = null;
            _touchDown = false;
            _hideAt = 0f;
            _suppressedBecauseEmpty = false;
        }

        void FailBackend(Exception e)
        {
            Debug.LogWarning($"[NativeTooltips] Backend '{_backend?.Name}' failed, switching to fallback.\n{e}");
            try { _backend?.Shutdown(); } catch { /* ignored */ }

            _phase = Phase.Idle;
            _source = null;
            bool previous = TooltipSettings.ForceFallback;
            TooltipSettings.ForceFallback = true;
            _backend = BackendFactory.Create();
            TooltipSettings.ForceFallback = previous;
        }

        ITooltipSource DeepestHovered()
        {
            ITooltipSource best = null;
            int bestDepth = int.MinValue;
            for (int i = _hovered.Count - 1; i >= 0; i--)
            {
                ITooltipSource s = _hovered[i];
                if (!IsAlive(s))
                {
                    _hovered.RemoveAt(i);
                    continue;
                }

                int depth = s is IHierarchicalTooltipSource h ? h.HierarchyDepth : 0;
                if (depth > bestDepth)
                {
                    best = s;
                    bestDepth = depth;
                }
            }

            return best;
        }

        static bool IsAlive(ITooltipSource s)
        {
            if (s == null) return false;
            if (s is UnityEngine.Object o && o == null) return false; // destroyed component
            try { return s.IsTooltipActive; }
            catch { return false; }
        }

        static string SafeText(ITooltipSource s)
        {
            try { return s?.TooltipText; }
            catch (Exception e)
            {
                Debug.LogException(e);
                return null;
            }
        }

        static bool SafeRect(ITooltipSource s, out Rect rect)
        {
            try { return s.TryGetScreenRect(out rect); }
            catch
            {
                rect = default;
                return false;
            }
        }

        float InitialDelay => TooltipSettings.InitialDelayOverride >= 0f
            ? TooltipSettings.InitialDelayOverride
            : _backend.InitialDelay;

        float AutoPopDelay => TooltipSettings.AutoPopDelayOverride >= 0f
            ? TooltipSettings.AutoPopDelayOverride
            : _backend.AutoPopDelay;

        float LongPressTimeout => TooltipSettings.LongPressTimeoutOverride >= 0f
            ? TooltipSettings.LongPressTimeoutOverride
            : _backend.LongPressTimeout;

        static float DragThreshold
        {
            get
            {
#if NATIVE_TOOLTIPS_UGUI
                var es = EventSystem.current;
                if (es != null) return Mathf.Max(1, es.pixelDragThreshold);
#endif
                return 10f;
            }
        }
    }
}

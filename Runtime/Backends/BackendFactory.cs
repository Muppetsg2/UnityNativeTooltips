using System;
using UnityEngine;

namespace NativeTooltips.Backends
{
    internal static class BackendFactory
    {
        public static ITooltipBackend Create()
        {
            if (!TooltipSettings.ForceFallback)
            {
                ITooltipBackend native = CreateNativeForCurrentPlatform();
                if (native != null)
                {
                    if (TryInit(native)) return native;
                    if (!TooltipSettings.UseFallbackWhenNativeUnavailable) return new NullTooltipBackend();
                }
            }

            var fallback = new ImguiTooltipBackend();
            fallback.Initialize();
            Log($"Using backend: {fallback.Name}");
            return fallback;
        }

        static ITooltipBackend CreateNativeForCurrentPlatform()
        {
#if UNITY_EDITOR
            if (!TooltipSettings.UseNativeInEditor) return null;
  #if UNITY_EDITOR_WIN
            return new WindowsTooltipBackend();
  #elif UNITY_EDITOR_LINUX
            return new LinuxGtkTooltipBackend();
  #else
            return null; // macOS: the AppKit bubble is bound to the player's view; the Editor uses IMGUI
  #endif
#elif UNITY_STANDALONE_WIN
            return new WindowsTooltipBackend();
#elif UNITY_STANDALONE_OSX
            return new MacOSTooltipBackend();
#elif UNITY_STANDALONE_LINUX
            return new LinuxGtkTooltipBackend();
#elif UNITY_ANDROID
            return new AndroidTooltipBackend();
#elif UNITY_IOS
            return new IOSTooltipBackend();
#elif UNITY_WEBGL
            return new WebGLTooltipBackend();
#else
            return null;
#endif
        }

        static bool TryInit(ITooltipBackend backend)
        {
            try
            {
                if (backend.Initialize())
                {
                    Log($"Using backend: {backend.Name}");
                    return true;
                }

                Debug.LogWarning($"[NativeTooltips] Native backend '{backend.Name}' is not available on this system — falling back.");
            }
            catch (DllNotFoundException e)
            {
                Debug.LogWarning($"[NativeTooltips] Native library missing for '{backend.Name}': {e.Message}");
            }
            catch (EntryPointNotFoundException e)
            {
                Debug.LogWarning($"[NativeTooltips] Native entry point missing for '{backend.Name}': {e.Message}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NativeTooltips] Failed to initialize '{backend.Name}': {e}");
            }

            try { backend.Shutdown(); } catch { /* ignored */ }
            return false;
        }

        internal static void Log(string message)
        {
            if (TooltipSettings.VerboseLogging) Debug.Log("[NativeTooltips] " + message);
        }
    }
}

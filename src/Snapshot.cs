using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

namespace MonstrumMac
{
    // Writes what is on screen and which shader each material really uses, so a missing or broken effect
    // can be traced: press the snapshot key (F8 by default) or create a file named "snapshot" in the cache folder.
    // Writes the full shader audit (audit.txt) at the same time.
    internal static class Snapshot
    {
        private const float Radius = 40f;
        private const int MaxRenderers = 3000;
        private static KeyCode key = KeyCode.F8;
        private static Func<bool> pressed;
        private static float nextFileCheck;

        internal static void Init(KeyCode snapshotKey)
        {
            key = snapshotKey;
            pressed = null;
        }

        internal static void Update()
        {
            try
            {
                bool now = KeyDown();
                if (!now && Time.realtimeSinceStartup >= nextFileCheck)
                {
                    nextFileCheck = Time.realtimeSinceStartup + 2f;
                    var flag = Path.Combine(Plugin.CacheDir, "snapshot");
                    if (File.Exists(flag)) { File.Delete(flag); now = true; }
                }
                if (now) Write();
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static bool KeyDown()
        {
            if (key == KeyCode.None) return false;
            if (pressed == null) pressed = KeyCheck();
            try { return pressed(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Snapshot key {key} disabled: {e.Message}");
                pressed = () => false;
                return false;
            }
        }

        // The legacy Input class throws when the game runs only the new Input System, so fall back to that by reflection.
        private static Func<bool> KeyCheck()
        {
            try
            {
                Input.GetKeyDown(key);
                return () => Input.GetKeyDown(key);
            }
            catch (Exception) { }
            try
            {
                var keyboard = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", true);
                var current = keyboard.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                var name = key.ToString();
                var control = keyboard.GetProperty(char.ToLowerInvariant(name[0]) + name.Substring(1) + "Key", BindingFlags.Public | BindingFlags.Instance);
                var down = control.PropertyType.GetProperty("wasPressedThisFrame", BindingFlags.Public | BindingFlags.Instance);
                if (current == null || down == null) throw new MissingMemberException("Keyboard." + name);
                return () =>
                {
                    var board = current.GetValue(null, null);
                    return board != null && (bool)down.GetValue(control.GetValue(board, null), null);
                };
            }
            catch (Exception) { }
            Plugin.Log.LogWarning($"Snapshot key {key} is not available; create a file named \"snapshot\" in {Plugin.CacheDir} instead");
            return () => false;
        }

        private static void Write()
        {
            var path = Path.Combine(Plugin.CacheDir, "snapshot.txt");
            var cam = Camera.main;
            var origin = cam != null ? cam.transform.position : Vector3.zero;
            var sb = new StringBuilder();
            sb.AppendLine($"Monstrum Mac {Version()} snapshot at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(cam != null ? $"Camera {cam.name} at {origin}" : "No main camera, listing visible renderers only");
            sb.AppendLine(Bundles.Describe());
            var shown = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r != null)
                .Select(r => new KeyValuePair<Renderer, float>(r, Vector3.Distance(origin, r.bounds.center)))
                .Where(p => p.Key.isVisible || (cam != null && p.Value <= Radius))
                .OrderBy(p => p.Value)
                .ToList();
            int listed = Math.Min(shown.Count, MaxRenderers);
            sb.AppendLine($"{shown.Count} renderers visible or within {Radius} m{(shown.Count > listed ? $", nearest {listed} listed" : "")}");
            sb.AppendLine();
            foreach (var pair in shown.Take(listed))
            {
                try { Append(sb, pair.Key, pair.Value); }
                catch (Exception e) { sb.AppendLine($"    (could not read this renderer: {e.Message})"); }
            }
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo($"Saved a snapshot of {listed} renderers to {path}");
            try { Fallback.Audit(Path.Combine(Plugin.CacheDir, "audit.txt")); }
            catch (Exception e) { Plugin.Log.LogError(e); }
            Show($"Monstrum Mac: snapshot saved ({listed} objects)");
        }

        private static void Append(StringBuilder sb, Renderer r, float distance)
        {
            var line = $"{distance,7:0.0} m  {PathOf(r.transform)}  {r.GetType().Name}  enabled={r.enabled}";
            if (r is ParticleSystemRenderer)
            {
                var ps = r.GetComponent<ParticleSystem>();
                if (ps != null) line += $"  playing={ps.isPlaying} particles={ps.particleCount}";
            }
            sb.AppendLine(line);
            foreach (var mat in r.sharedMaterials) sb.AppendLine("    " + Describe(mat));
        }

        private static string Describe(Material mat)
        {
            if (mat == null) return "(no material)";
            var sb = new StringBuilder(mat.name);
            var s = mat.shader;
            if (s == null) return sb.Append("  shader=(none)").ToString();
            sb.Append($"  shader={s.name} #{s.GetInstanceID()} {Fallback.State(s)}");
            if (Bundles.FromMod(s)) sb.Append(" mod copy");
            var was = Fallback.OriginalShader(mat);
            if (was != null) sb.Append($"  was {was}");
            sb.Append($"  queue={mat.renderQueue}");
            var keywords = mat.shaderKeywords;
            if (keywords != null && keywords.Length > 0) sb.Append($"  kw={string.Join(" ", keywords)}");
            if (mat.HasProperty("_SrcBlend") && mat.HasProperty("_DstBlend")) sb.Append($"  blend={mat.GetFloat("_SrcBlend")}/{mat.GetFloat("_DstBlend")}");
            if (mat.HasProperty("_Color")) sb.Append($"  color={mat.GetColor("_Color")}");
            if (mat.HasProperty("_TintColor")) sb.Append($"  tint={mat.GetColor("_TintColor")}");
            if (mat.HasProperty("_MainTex"))
            {
                var tex = mat.GetTexture("_MainTex");
                sb.Append($"  tex={(tex != null ? tex.name : "none")}");
            }
            return sb.ToString();
        }

        private static string PathOf(Transform t)
        {
            var path = t.name;
            while ((t = t.parent) != null) path = t.name + "/" + path;
            return path;
        }

        private static string Version()
        {
            var info = (BepInPlugin)Attribute.GetCustomAttribute(typeof(Plugin), typeof(BepInPlugin));
            return info != null ? info.Version.ToString() : "?";
        }

        // Best effort: a line in Valheim's top-left message area, so it is clear the key worked.
        private static void Show(string text)
        {
            try
            {
                var hud = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == "assembly_valheim")
                    .Select(a => a.GetType("MessageHud"))
                    .FirstOrDefault(t => t != null);
                if (hud == null) return;
                const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var instance = hud.GetProperty("instance", statics)?.GetValue(null, null) ?? hud.GetField("instance", statics)?.GetValue(null);
                if (instance == null) return;
                var show = hud.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return m.Name == "ShowMessage" && p.Length >= 2 && p[0].ParameterType.IsEnum && p[1].ParameterType == typeof(string);
                });
                if (show == null) return;
                var ps = show.GetParameters();
                var args = new object[ps.Length];
                args[0] = Enum.Parse(ps[0].ParameterType, "TopLeft");
                args[1] = text;
                for (int i = 2; i < ps.Length; i++)
                {
                    var type = ps[i].ParameterType;
                    var value = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                    if (value == null && type.IsValueType) value = Activator.CreateInstance(type);
                    else if (value != null && type.IsEnum && !type.IsInstanceOfType(value)) value = Enum.ToObject(type, value);
                    args[i] = value;
                }
                show.Invoke(instance, args);
            }
            catch (Exception e) { Plugin.Log.LogDebug("Could not show the snapshot message: " + e.Message); }
        }
    }
}

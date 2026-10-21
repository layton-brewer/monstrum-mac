using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MonstrumMac
{
    // Makes mod materials whose shaders have no Metal code draw on a Mac, by moving them to working shaders.
    // Can also swap asset bundles for converted copies that carry Metal shader variants, when such copies exist in
    // ~/Library/Caches/Monstrum-Mac/<sha256 of original>.bundle
    [BepInPlugin("Monstrum-Mac", "Monstrum Mac", "1.0.3")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static string CacheDir;
        private static bool logEachMaterial;
        private static float maxInterval;

        private void Awake()
        {
            Log = Logger;
            CacheDir = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library/Caches/Monstrum-Mac");
            var on = Config.Bind("General", "Enabled", true, "Turn the plugin off without uninstalling it").Value;
            logEachMaterial = Config.Bind("Diagnostics", "LogEachMaterial", false, "Write a line to the BepInEx log for every material moved to another shader").Value;
            maxInterval = Mathf.Max(60f, Config.Bind("Performance", "MaxSweepInterval", 600f, "Longest wait, in seconds, between material checks once nothing new has needed fixing. Mod bundle loads and new scenes always trigger a check straight away").Value);
            var key = Config.Bind("Diagnostics", "SnapshotKey", KeyCode.F8, "Writes snapshot.txt and audit.txt in ~/Library/Caches/Monstrum-Mac, listing what is on screen and the shader each material really uses").Value;
            // Only Metal (macOS) lacks the mods' shader code; on DirectX, Vulkan or OpenGL there is nothing to fix.
            var device = SystemInfo.graphicsDeviceType;
            if (!on || device != GraphicsDeviceType.Metal)
            {
                Log.LogInfo(on ? $"Graphics API is {device}, not Metal, so mod shaders already work here: staying idle" : "Turned off in the config");
                enabled = false;
                return;
            }
            // snapshot.txt and audit.txt go here, and writing a file doesn't create its folder.
            try { Directory.CreateDirectory(CacheDir); }
            catch (Exception e) { Log.LogWarning("Could not create the cache folder: " + e.Message); }
            var harmony = new Harmony("Monstrum-Mac");
            // Hashing every mod bundle as it loads costs seconds at startup, so only do it when there is a copy to swap in.
            if (HasConvertedBundles())
            {
                Patch(harmony, "LoadFromStreamInternal", nameof(StreamPrefix));
                Patch(harmony, "LoadFromStreamAsyncInternal", nameof(StreamPrefix));
                Patch(harmony, "LoadFromMemory_Internal", nameof(MemoryPrefix));
                Patch(harmony, "LoadFromMemoryAsync_Internal", nameof(MemoryPrefix));
            }
            else Log.LogDebug("No converted bundles in the cache folder, so mod bundles load unchanged");
            Bundles.Patch(harmony);
            Snapshot.Init(key);
            Log.LogDebug("Cache folder: " + CacheDir);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // One line per moved material: useful when tracking down a wrong-looking effect, noise otherwise.
        internal static void Detail(string message)
        {
            if (logEachMaterial) Log.LogInfo(message);
            else Log.LogDebug(message);
        }

        private static bool HasConvertedBundles()
        {
            try { return Directory.Exists(CacheDir) && Directory.GetFiles(CacheDir, "*.bundle").Length > 0; }
            catch (Exception e) { Log.LogWarning("Could not look for converted bundles: " + e.Message); return true; }
        }

        private float nextSweep = 5f;
        private float interval = 10f;
        private Coroutine sweep;
        private int lastStep;
        private bool sweeping, sceneLoaded;

        // Sweeps often while mods are loading, then backs off to MaxSweepInterval (each check walks every
        // material, which costs a few ms). New mod assets and new scenes bring the next sweep forward.
        private void Update()
        {
            Snapshot.Update();
            if (sweeping)
            {
                // A sweep moves on every frame, so one that hasn't for a while had its coroutine stopped: start over.
                if (Time.frameCount - lastStep < 600) return;
                if (sweep != null) StopCoroutine(sweep);
                sweeping = false;
            }
            if (sceneLoaded || Bundles.NewAssets)
            {
                nextSweep = Mathf.Min(nextSweep, Time.realtimeSinceStartup + (sceneLoaded ? 2f : 3f));
                interval = 10f;
                sceneLoaded = Bundles.NewAssets = false;
            }
            if (Time.realtimeSinceStartup < nextSweep) return;
            sweep = StartCoroutine(RunSweep());
        }

        // Runs one sweep spread over as many frames as it needs, and reports what it cost.
        private IEnumerator RunSweep()
        {
            sweeping = true;
            lastStep = Time.frameCount;
            try
            {
                var busy = new System.Diagnostics.Stopwatch();
                int frames = 0, probes = Fallback.Probes, alphaProbes = Fallback.AlphaProbes;
                long probeMs = Fallback.ProbeTime.ElapsedMilliseconds, alphaMs = Fallback.AlphaTime.ElapsedMilliseconds;
                var steps = Fallback.Sweep();
                while (true)
                {
                    bool more;
                    busy.Start();
                    try { more = steps.MoveNext(); }
                    catch (Exception e) { Log.LogError(e); more = false; }
                    busy.Stop();
                    frames++;
                    lastStep = Time.frameCount;
                    if (!more) break;
                    yield return null;
                }
                int n = Fallback.LastMoved;
                interval = n > 0 ? 10f : Mathf.Min(interval * 2f, maxInterval);
                nextSweep = Time.realtimeSinceStartup + interval;
                if (n > 0) Log.LogInfo($"Moved {n} materials to a working shader ({busy.ElapsedMilliseconds} ms of work over {frames} frames)");
                else Log.LogDebug($"Sweep: nothing to move ({busy.ElapsedMilliseconds} ms of work over {frames} frames)");
                if (Fallback.Probes > probes || Fallback.AlphaProbes > alphaProbes)
                    Log.LogInfo($"Test-drew {Fallback.Probes - probes} shaders ({Fallback.ProbeTime.ElapsedMilliseconds - probeMs} ms) and {Fallback.AlphaProbes - alphaProbes} see-through setups ({Fallback.AlphaTime.ElapsedMilliseconds - alphaMs} ms)");
            }
            finally { sweeping = false; }
        }

        // A new scene brings new materials, so check them soon instead of waiting for the next sweep.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => sceneLoaded = true;

        // The audit walks every material and renderer, so it is written with a snapshot or when the game quits, never mid-game.
        private void OnApplicationQuit()
        {
            if (!enabled) return;
            try { Fallback.Audit(Path.Combine(CacheDir, "audit.txt")); }
            catch (Exception e) { Log.LogError(e); }
        }

        private static void Patch(Harmony harmony, string target, string prefix)
        {
            var original = AccessTools.Method(typeof(AssetBundle), target);
            if (original == null) { Log.LogWarning("Not found: AssetBundle." + target); return; }
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(Plugin), prefix));
            Log.LogDebug("Hooked AssetBundle." + target);
        }

        internal static byte[] Lookup(byte[] original)
        {
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(original)).Replace("-", "").ToLowerInvariant();
            var path = Path.Combine(CacheDir, hash + ".bundle");
            if (!File.Exists(path)) { Log.LogDebug("No converted copy for bundle " + hash.Substring(0, 12)); return null; }
            Log.LogInfo("Using converted bundle " + hash.Substring(0, 12));
            return File.ReadAllBytes(path);
        }

        private static void StreamPrefix(ref Stream stream)
        {
            try
            {
                if (stream == null || !stream.CanRead || !stream.CanSeek) return;
                long start = stream.Position;
                var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                stream.Position = start;
                var replacement = Lookup(buffer.ToArray());
                if (replacement != null) stream = new MemoryStream(replacement, false);
            }
            catch (Exception e) { Log.LogError(e); }
        }

        private static void MemoryPrefix(ref byte[] binary)
        {
            try
            {
                if (binary == null) return;
                var replacement = Lookup(binary);
                if (replacement != null) binary = replacement;
            }
            catch (Exception e) { Log.LogError(e); }
        }
    }
}

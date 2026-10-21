using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonstrumMac
{
    // Remembers which shader instances came out of mod asset bundles. A mod built on Windows ships no Metal
    // code, and its copy of a shader like Standard can quietly draw with an old fallback instead of pink,
    // so the test-draw alone can't tell it from the game's real copy of the same name.
    internal static class Bundles
    {
        private class Id { public int Bundle; }

        // Bundles loaded by mods (from memory, a stream, or a file outside the game's own data folder).
        private static readonly HashSet<int> mods = new HashSet<int>();
        private static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        // Mod bundles known to have no Metal code, because one of their shaders failed to draw.
        private static readonly HashSet<int> noMetal = new HashSet<int>();
        // Shader instance id -> the mod bundle it was first seen in.
        private static readonly Dictionary<int, int> shaderBundle = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> shaderCount = new Dictionary<int, int>();
        // Materials already examined. Only the first sighting counts: by a later load a mod may have
        // pointed the material at one of the game's shaders, which must not be blamed on the bundle.
        private static readonly HashSet<int> seen = new HashSet<int>();
        // Prefabs already walked, so a mod that loads the same prefab again and again costs nothing.
        private static readonly HashSet<int> walked = new HashSet<int>();
        private static readonly ConditionalWeakTable<AssetBundleCreateRequest, Id> creating = new ConditionalWeakTable<AssetBundleCreateRequest, Id>();
        private static readonly ConditionalWeakTable<AssetBundleRequest, Id> loading = new ConditionalWeakTable<AssetBundleRequest, Id>();
        private static string gameData;

        // Set whenever new mod materials were seen, so the plugin can sweep soon.
        internal static bool NewAssets;

        internal static void Patch(Harmony harmony)
        {
            try { gameData = Path.GetFullPath(Application.dataPath); }
            catch (Exception e) { Plugin.Log.LogWarning("Game data folder unknown, so bundles loaded from files are not tracked: " + e.Message); }
            Postfix(harmony, typeof(AssetBundle), "LoadFromMemory_Internal", nameof(BundleLoaded));
            Postfix(harmony, typeof(AssetBundle), "LoadFromStreamInternal", nameof(BundleLoaded));
            Postfix(harmony, typeof(AssetBundle), "LoadFromFile_Internal", nameof(FileBundleLoaded));
            Postfix(harmony, typeof(AssetBundle), "LoadFromMemoryAsync_Internal", nameof(BundleRequested));
            Postfix(harmony, typeof(AssetBundle), "LoadFromStreamAsyncInternal", nameof(BundleRequested));
            Postfix(harmony, typeof(AssetBundle), "LoadFromFileAsync_Internal", nameof(FileBundleRequested));
            Postfix(harmony, typeof(AssetBundleCreateRequest), "get_assetBundle", nameof(BundleReady));
            Postfix(harmony, typeof(AssetBundle), "LoadAsset_Internal", nameof(AssetLoaded));
            Postfix(harmony, typeof(AssetBundle), "LoadAssetWithSubAssets_Internal", nameof(AssetsLoaded));
            Postfix(harmony, typeof(AssetBundle), "LoadAssetAsync_Internal", nameof(AssetRequested));
            Postfix(harmony, typeof(AssetBundle), "LoadAssetWithSubAssetsAsync_Internal", nameof(AssetRequested));
            Postfix(harmony, typeof(AssetBundleRequest), "GetResult", nameof(AssetReady));
            Postfix(harmony, typeof(AssetBundleRequest), "get_allAssets", nameof(AssetsReady));
        }

        internal static bool IsModMaterial(Material m) => m != null && seen.Contains(m.GetInstanceID());

        internal static bool FromMod(Shader s) => s != null && shaderBundle.ContainsKey(s.GetInstanceID());

        internal static bool FromNoMetalBundle(Shader s) =>
            s != null && shaderBundle.TryGetValue(s.GetInstanceID(), out var bundle) && noMetal.Contains(bundle);

        // A shader from this bundle failed to draw, so the bundle was built without Metal and none of its shaders can run here.
        internal static void Broken(Shader s)
        {
            if (s == null || !shaderBundle.TryGetValue(s.GetInstanceID(), out var bundle) || !noMetal.Add(bundle)) return;
            shaderCount.TryGetValue(bundle, out var n);
            names.TryGetValue(bundle, out var name);
            Plugin.Log.LogInfo($"Bundle {name} #{bundle} has no Metal shaders ({s.name} failed); treating its {n} shaders as broken");
        }

        internal static string Describe() =>
            $"Mod bundles: {mods.Count} tracked, {noMetal.Count} without Metal; {shaderBundle.Count} shaders and {seen.Count} materials attributed to them";

        private static void Postfix(Harmony harmony, Type type, string target, string postfix)
        {
            try
            {
                var original = AccessTools.DeclaredMethod(type, target);
                if (original == null) { Plugin.Log.LogWarning($"Not found: {type.Name}.{target}"); return; }
                harmony.Patch(original, postfix: new HarmonyMethod(typeof(Bundles), postfix));
                Plugin.Log.LogDebug($"Hooked {type.Name}.{target}");
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        // Valheim loads its own bundles from files inside its data folder; anything else loaded from a file is a mod's.
        private static bool IsModFile(object[] args)
        {
            if (gameData == null || args == null || args.Length == 0 || !(args[0] is string path) || path.Length == 0) return false;
            var full = Path.GetFullPath(path);
            return !full.StartsWith(gameData, StringComparison.OrdinalIgnoreCase) && full.IndexOf("StreamingAssets", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void Register(AssetBundle bundle)
        {
            if (bundle == null) return;
            int id = bundle.GetInstanceID();
            if (!mods.Add(id)) return;
            names[id] = bundle.name;
            Plugin.Log.LogDebug($"Tracking mod bundle {bundle.name} #{id}");
        }

        private static void BundleLoaded(AssetBundle __result)
        {
            try { Register(__result); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void FileBundleLoaded(object[] __args, AssetBundle __result)
        {
            try { if (__result != null && IsModFile(__args)) Register(__result); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void BundleRequested(AssetBundleCreateRequest __result)
        {
            try { if (__result != null) Remember(creating, __result, 0); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void FileBundleRequested(object[] __args, AssetBundleCreateRequest __result)
        {
            try { if (__result != null && IsModFile(__args)) Remember(creating, __result, 0); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void BundleReady(AssetBundleCreateRequest __instance, AssetBundle __result)
        {
            try { if (__result != null && creating.TryGetValue(__instance, out _)) Register(__result); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void AssetLoaded(AssetBundle __instance, Object __result)
        {
            try { if (IsMod(__instance, out var bundle)) Mark(__result, bundle); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void AssetsLoaded(AssetBundle __instance, Object[] __result)
        {
            try { if (IsMod(__instance, out var bundle)) Mark(__result, bundle); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void AssetRequested(AssetBundle __instance, AssetBundleRequest __result)
        {
            try { if (__result != null && IsMod(__instance, out var bundle)) Remember(loading, __result, bundle); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void AssetReady(AssetBundleRequest __instance, Object __result)
        {
            try { if (__instance != null && loading.TryGetValue(__instance, out var id)) Mark(__result, id.Bundle); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static void AssetsReady(AssetBundleRequest __instance, Object[] __result)
        {
            try { if (__instance != null && loading.TryGetValue(__instance, out var id)) Mark(__result, id.Bundle); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static bool IsMod(AssetBundle bundle, out int id)
        {
            id = bundle == null ? 0 : bundle.GetInstanceID();
            return id != 0 && mods.Contains(id);
        }

        private static void Remember<T>(ConditionalWeakTable<T, Id> table, T key, int bundle) where T : class
        {
            table.Remove(key);
            table.Add(key, new Id { Bundle = bundle });
        }

        private static void Mark(object asset, int bundle)
        {
            if (asset == null || (asset is Object unityObject && unityObject == null)) return;
            switch (asset)
            {
                case Object[] list:
                    foreach (var item in list) Mark(item, bundle);
                    break;
                case GameObject go:
                    if (!walked.Add(go.GetInstanceID())) break;
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials) MarkMaterial(m, bundle);
                    break;
                case Component c:
                    Mark(c.gameObject, bundle);
                    break;
                case Material m:
                    MarkMaterial(m, bundle);
                    break;
                case Shader s:
                    Attribute(s, bundle);
                    break;
            }
        }

        private static void MarkMaterial(Material m, int bundle)
        {
            if (m == null || !seen.Add(m.GetInstanceID())) return;
            NewAssets = true;
            Attribute(m.shader, bundle);
        }

        private static void Attribute(Shader s, int bundle)
        {
            if (s == null) return;
            int id = s.GetInstanceID();
            if (shaderBundle.ContainsKey(id)) return;
            var name = s.name;
            if (name == "Hidden/InternalErrorShader") return;
            shaderBundle[id] = bundle;
            shaderCount.TryGetValue(bundle, out var n);
            shaderCount[bundle] = n + 1;
            NewAssets = true;
        }
    }
}

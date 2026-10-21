using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonstrumMac
{
    // Last tier: any material whose shader cannot run on Metal gets the game's copy of the same
    // shader, or the closest safe shader, so it never renders pink.
    internal static class Fallback
    {
        // Original is a shader name, or a material name when Twin is set.
        private class StandIn { public string Original; public bool Twin; public Shader Used; }

        private enum AlphaMode { None, Cutout, Blend }

        // How a material was set up before this plugin first touched it, so later moves (stand-in to the
        // game's shader) still know its keywords, colour and blending.
        private class Setup
        {
            public string Shader;
            public string[] Keywords;
            public int Queue;
            public Color? Color, Tint;
            public bool HadBlendProps;
            public int Src = 1, Dst = 0;
            public AlphaMode Mode;
        }

        // Shader instance id -> whether its test draws came out right. Mod shaders with no Metal code often
        // still report isSupported, so each shader is test-drawn once and checked for the pink error colour.
        private static readonly Dictionary<int, bool> works = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> errorShaders = new Dictionary<int, bool>();
        private static Dictionary<string, List<Shader>> byName;
        private static float lastRefresh = -100f;
        // Materials given a stand-in because the game's own copy of their shader wasn't loaded yet.
        private static readonly Dictionary<Material, StandIn> standIns = new Dictionary<Material, StandIn>();
        // Materials this plugin changed, and working materials it never touched (mostly the game's), by name.
        private static readonly HashSet<Material> touched = new HashSet<Material>();
        private static readonly Dictionary<Material, Setup> originals = new Dictionary<Material, Setup>();
        private static Dictionary<string, Material> named = new Dictionary<string, Material>();
        // Results of the see-through test, by shader and material setup.
        private static readonly Dictionary<string, bool> alphaTests = new Dictionary<string, bool>();
        private static RenderTexture probeTarget, alphaTarget;
        private static Texture2D probePixels, alphaPixels, clearTexture;
        private static Mesh quad;
        private static Color? backdrop;
        // Pink tests and see-through tests run so far, and the time they took.
        internal static int Probes, AlphaProbes;
        internal static readonly Stopwatch ProbeTime = new Stopwatch(), AlphaTime = new Stopwatch();
        private const string Additive = "Legacy Shaders/Particles/Additive";
        private const string AlphaBlended = "Legacy Shaders/Particles/Alpha Blended";

        // These have see-through variants in the game's own copy, so a copy that can't draw see-through is
        // showing its fallback shader (usually the old opaque VertexLit) instead of itself.
        private static readonly HashSet<string> selfTested = new HashSet<string> { "Standard", "Autodesk Interactive", "Particles/Standard Unlit" };

        // How many materials the last sweep moved.
        internal static int LastMoved;
        // Work done per frame before a sweep waits for the next one, so it never causes a stutter. A test draw
        // makes its frame wait for the GPU anyway, so once one has run, more of them share that frame (up to a
        // bigger budget) instead of each stalling a frame of its own.
        private const double SliceMs = 1.0, TestSliceMs = 10.0;
        private static int namedFrom = -1;

        private class Slice
        {
            private readonly Stopwatch watch = Stopwatch.StartNew();
            private int draws = Probes + AlphaProbes;
            internal bool Full => watch.Elapsed.TotalMilliseconds > (Probes + AlphaProbes != draws ? TestSliceMs : SliceMs);
            internal void Next() { watch.Restart(); draws = Probes + AlphaProbes; }
        }

        // Sweeps every loaded material, a little each frame. Runs periodically so materials from bundles and
        // instantiated copies are both covered, and game shaders that load late get used.
        internal static IEnumerator Sweep()
        {
            int count = 0;
            var slice = new Slice();
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            if (materials.Length != particlesFrom)
            {
                particlesFrom = materials.Length;
                particleMaterials = ParticleMaterials();
            }
            // Test-draw the new shaders first without waiting on the GPU, so the checks below find their answers ready.
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                var ahead = ProbeAhead(materials, slice);
                while (ahead.MoveNext()) yield return ahead.Current;
            }
            List<Material> missing = null;
            foreach (var mat in materials)
            {
                if (slice.Full) { yield return null; slice.Next(); }
                switch (Check(mat))
                {
                    case Result.Moved: count++; break;
                    case Result.Missing: (missing ?? (missing = new List<Material>())).Add(mat); break;
                }
            }
            // The names are only needed to find the game's copy of a material, and only change when materials load.
            if ((missing != null || standIns.Values.Any(v => v.Twin)) && materials.Length != namedFrom)
            {
                namedFrom = materials.Length;
                var map = new Dictionary<string, Material>();
                foreach (var mat in materials)
                {
                    if (slice.Full) { yield return null; slice.Next(); }
                    AddNamed(map, mat);
                }
                named = map;
            }
            if (missing != null)
            {
                foreach (var mat in missing)
                {
                    if (slice.Full) { yield return null; slice.Next(); }
                    try { if (mat != null && mat.shader != null && IsMissing(mat.shader) && FixMissing(mat)) count++; }
                    catch (Exception e) { Plugin.Log.LogError(e); }
                }
            }
            foreach (var pair in standIns.ToList())
            {
                if (slice.Full) { yield return null; slice.Next(); }
                try { if (Upgrade(pair.Key, pair.Value)) count++; }
                catch (Exception e) { Plugin.Log.LogError(e); }
            }
            LastMoved = count;
        }

        private enum Result { Nothing, Moved, Missing }

        private static Result Check(Material mat)
        {
            try
            {
                if (mat == null) return Result.Nothing;
                var shader = mat.shader;
                if (shader == null) return Result.Nothing;
                // A placeholder without a name can't be matched to the game's material, so leave it.
                if (IsMissing(shader)) return string.IsNullOrEmpty(mat.name) ? Result.Nothing : Result.Missing;
                return FixMaterial(mat, shader) ? Result.Moved : Result.Nothing;
            }
            catch (Exception e) { Plugin.Log.LogError(e); return Result.Nothing; }
        }

        private static void AddNamed(Dictionary<string, Material> map, Material mat)
        {
            try
            {
                if (mat == null || mat.shader == null || touched.Contains(mat) || Bundles.IsModMaterial(mat) || !Works(mat.shader)) return;
                var name = mat.name;
                if (!map.ContainsKey(name)) map[name] = mat;
            }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }

        private static bool FixMaterial(Material mat, Shader shader)
        {
            if (Works(shader)) return false;
            var from = shader.name;
            var target = Find(from);
            var standIn = target == null;
            if (standIn)
            {
                // Draws fine but came from a bundle without Metal code, so it is most likely showing its
                // fallback shader. Only move the mods' own materials then, in case that guess is wrong.
                if (!Bundles.IsModMaterial(mat) && Proven(shader)) return false;
                target = FindAny(Substitutes(from, OriginalOf(mat)));
            }
            if (target == null) return false; // try again on a later sweep, once the game has loaded it
            var used = Apply(mat, target);
            if (standIn) standIns[mat] = new StandIn { Original = from, Used = used };
            Plugin.Detail($"Material {mat.name}: {from} -> {used.name}{(standIn ? " (until the game loads its own copy)" : "")}");
            return true;
        }

        // Mods sometimes ship placeholder copies of the game's own materials whose shader is missing
        // from the bundle. Copy the game's material of the same name, or use a stand-in until it loads.
        private static bool FixMissing(Material mat)
        {
            if (string.IsNullOrEmpty(mat.name)) return false;
            if (named.TryGetValue(mat.name, out var twin) && twin != null)
            {
                OriginalOf(mat);
                CopyTwin(mat, twin);
                Plugin.Detail($"Material {mat.name}: shader missing -> copied the game's material ({twin.shader.name})");
                return true;
            }
            var target = FindAny(Guess(mat.name), "Standard");
            if (target == null) return false;
            var used = Apply(mat, target);
            standIns[mat] = new StandIn { Original = mat.name, Twin = true, Used = used };
            Plugin.Detail($"Material {mat.name}: shader missing -> {used.name} (until the game loads its own copy)");
            return true;
        }

        // Moves a stand-in to the game's own shader or material once it has loaded.
        private static bool Upgrade(Material mat, StandIn info)
        {
            if (mat == null || mat.shader != info.Used) { standIns.Remove(mat); return false; }
            if (info.Twin)
            {
                if (!named.TryGetValue(info.Original, out var twin) || twin == null) return false;
                CopyTwin(mat, twin);
            }
            else
            {
                var real = Find(info.Original);
                if (real == null) return false;
                Apply(mat, real);
            }
            standIns.Remove(mat);
            Plugin.Detail($"Material {mat.name}: now using the game's {mat.shader.name}");
            return true;
        }

        private static void CopyTwin(Material mat, Material twin)
        {
            mat.shader = twin.shader;
            mat.CopyPropertiesFromMaterial(twin);
            touched.Add(mat);
        }

        private static Setup OriginalOf(Material mat)
        {
            if (originals.TryGetValue(mat, out var setup)) return setup;
            var name = mat.shader.name;
            var lower = name.ToLowerInvariant();
            setup = new Setup
            {
                Shader = name,
                Keywords = mat.shaderKeywords ?? new string[0],
                Queue = mat.renderQueue,
                Color = mat.HasProperty("_Color") ? mat.color : (Color?)null,
                Tint = mat.HasProperty("_TintColor") ? mat.GetColor("_TintColor") : (Color?)null,
            };
            if (mat.HasProperty("_SrcBlend") && mat.HasProperty("_DstBlend"))
            {
                setup.HadBlendProps = true;
                setup.Src = Mathf.RoundToInt(mat.GetFloat("_SrcBlend"));
                setup.Dst = Mathf.RoundToInt(mat.GetFloat("_DstBlend"));
            }
            else if (lower.Contains("transparent") && !lower.Contains("cutout")) { setup.Src = 5; setup.Dst = 10; }
            // Foliage shaders cut leaves out of their texture without saying so.
            var cutout = setup.Keywords.Contains("_ALPHATEST_ON") || Has(lower, "cutout", "groundcover", "grass", "foliage", "leaf", "leaves", "vegetation")
                || Has(mat.name, "leaf", "leaves", "grass", "foliage");
            setup.Mode = setup.Dst != 0 ? AlphaMode.Blend : cutout ? AlphaMode.Cutout : AlphaMode.None;
            originals[mat] = setup;
            return setup;
        }

        // The shader a material had before this plugin changed it, or null if it was never changed.
        internal static string OriginalShader(Material mat) => mat != null && originals.TryGetValue(mat, out var setup) ? setup.Shader : null;

        // Moves the material to the target shader, keeping its textures, colour, keywords and draw order.
        // Returns the shader actually used.
        private static Shader Apply(Material mat, Shader target)
        {
            var original = OriginalOf(mat);
            var textures = mat.GetTexturePropertyNames().Select(n => new KeyValuePair<string, Texture>(n, mat.GetTexture(n))).Where(p => p.Value != null).ToList();
            var color = original.Color ?? Color.white;
            target = ForBlending(mat, target, original);
            mat.shader = target;
            // Exactly the original keywords, so a stand-in's see-through set-up doesn't stay on the game's shader.
            mat.shaderKeywords = original.Keywords;
            Configure(mat, original);
            if (original.Queue > 2500 && mat.renderQueue <= 2500) mat.renderQueue = original.Queue;
            if (mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") == null)
            {
                var albedo = textures.FirstOrDefault(p => Has(p.Key, "albedo", "diffuse", "basecolor", "base", "main", "color", "tex"));
                if (albedo.Value != null) mat.SetTexture("_MainTex", albedo.Value);
            }
            if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") == null)
            {
                var normal = textures.FirstOrDefault(p => Has(p.Key, "normal", "bump"));
                if (normal.Value != null) { mat.SetTexture("_BumpMap", normal.Value); mat.EnableKeyword("_NORMALMAP"); }
            }
            if (mat.HasProperty("_Color")) mat.color = color;
            if (mat.HasProperty("_TintColor"))
            {
                // A heat-haze effect bends the screen behind it, which no stand-in can do, so hide it instead of
                // showing a sprite. Legacy particle shaders draw 2 x tint x texture, so half the colour matches.
                if (target.name != original.Shader && Has(original.Shader, "distort")) mat.SetColor("_TintColor", Color.clear);
                else mat.SetColor("_TintColor", original.Tint ?? color * 0.5f);
            }
            touched.Add(mat);
            return target;
        }

        // Materials drawn by particle systems, trails and lines. Their quads get their shape from the texture's
        // alpha and their fade from vertex colours, which the lit Standard shaders don't use, so on one of those
        // they can show up as solid squares. They go to the game's legacy particle shaders instead.
        private static HashSet<Material> particleMaterials = new HashSet<Material>();
        private static int particlesFrom = -1;

        private static HashSet<Material> ParticleMaterials()
        {
            var set = new HashSet<Material>();
            void Add(Material m) { if (m != null) set.Add(m); }
            foreach (var r in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
            {
                foreach (var m in r.sharedMaterials) Add(m);
                Add(r.trailMaterial);
            }
            foreach (var r in Resources.FindObjectsOfTypeAll<TrailRenderer>()) foreach (var m in r.sharedMaterials) Add(m);
            foreach (var r in Resources.FindObjectsOfTypeAll<LineRenderer>()) foreach (var m in r.sharedMaterials) Add(m);
            return set;
        }

        // A see-through or cut-out material moved from a shader without blend settings (like the old
        // Transparent shaders) onto a Standard-style shader needs the Standard mode set up by hand.
        private static void Configure(Material mat, Setup original)
        {
            if (original.HadBlendProps || original.Mode == AlphaMode.None || !IsStandardLike(mat.shader)) return;
            var cutout = original.Mode == AlphaMode.Cutout;
            SetFloat(mat, "_Mode", cutout ? 1 : original.Src == 1 ? 3 : 2);
            SetFloat(mat, "_SrcBlend", cutout ? 1 : original.Src);
            SetFloat(mat, "_DstBlend", cutout ? 0 : original.Dst);
            SetFloat(mat, "_ZWrite", cutout ? 1 : 0);
            mat.EnableKeyword(cutout ? "_ALPHATEST_ON" : original.Src == 1 ? "_ALPHAPREMULTIPLY_ON" : "_ALPHABLEND_ON");
            mat.renderQueue = cutout ? 2450 : 3000;
        }

        private static void SetFloat(Material mat, string name, float value)
        {
            if (mat.HasProperty(name)) mat.SetFloat(name, value);
        }

        // Some of the game's Standard-style shaders only carry the variants its own materials use, so a
        // see-through material moved onto one could draw as solid squares. Check by drawing it, and use
        // Standard (which has every see-through variant) or a particle shader instead.
        private static Shader ForBlending(Material mat, Shader target, Setup original)
        {
            if (original.Mode == AlphaMode.None || !IsStandardLike(target)) return target;
            if (particleMaterials.Contains(mat) && !target.name.StartsWith("Particles/"))
            {
                var particle = FindAny(LegacyParticles(original));
                if (particle != null) return particle;
            }
            if (HandlesAlpha(target, mat, original)) return target;
            var standard = Find("Standard");
            if (standard != null && standard != target && HandlesAlpha(standard, mat, original)) return standard;
            if (original.Mode == AlphaMode.Cutout) return target;
            return FindAny(LegacyParticles(original)) ?? target;
        }

        // The legacy particle shader that blends the way this material did. Not the legacy premultiplied one: it
        // has no colour property, and Standard's premultiplied mode multiplies by alpha itself, like Alpha Blended.
        private static string[] LegacyParticles(Setup original) => new[] { original.Dst == 1 ? Additive : AlphaBlended };

        private static bool IsStandardLike(Shader s) => s.name.StartsWith("Standard") || s.name == "Autodesk Interactive" || s.name.StartsWith("Particles/Standard");

        private static bool Has(string s, params string[] words)
        {
            s = s.ToLowerInvariant();
            return words.Any(w => s.Contains(w));
        }

        // Stand-ins for a shader the game doesn't have, best first.
        private static string[] Substitutes(string name, Setup original)
        {
            var n = name.ToLowerInvariant();
            // Apply hides these: additive with no colour draws nothing.
            if (n.Contains("distort")) return new[] { Additive };
            if (n.StartsWith("particles/standard surface")) return new[] { "Particles/Standard Surface2" }.Concat(ParticlesFor(n, original)).ToArray();
            if (n.StartsWith("particles/standard unlit")) return new[] { "Particles/Standard Unlit2" }.Concat(ParticlesFor(n, original)).ToArray();
            if (n.Contains("particle") || n.Contains("hovl")) return ParticlesFor(n, original);
            if (n.Contains("unlit") && Has(n, "add", "glow", "blend", "transparent")) return ParticlesFor(n, original);
            if (n.Contains("double") || n.Contains("2-sided") || n.Contains("twosided") || n.Contains("two sided"))
                return new[] { "Standard TwoSided", "Standard" };
            if (n.StartsWith("legacy shaders/") && !n.Contains("transparent")) return new[] { "Legacy Shaders/Diffuse", "Standard" };
            return new[] { "Standard" };
        }

        // Legacy particle shaders that blend like the original: by its own blend settings when it has them, else
        // by its name ("Blend_CenterGlow" blends, "Add_CenterGlow" adds).
        private static string[] ParticlesFor(string n, Setup original)
        {
            if (original.HadBlendProps && original.Mode == AlphaMode.Blend) return LegacyParticles(original);
            if (n.Contains("blend")) return new[] { AlphaBlended };
            return new[] { n.Contains("add") || n.Contains("glow") ? Additive : AlphaBlended };
        }

        // A stand-in for a material whose shader is missing altogether, going by the material's name.
        private static string Guess(string name)
        {
            if (Has(name, "spark", "fire", "flame", "glow", "ember", "flare", "light", "magic", "beam", "trail"))
                return Additive;
            if (Has(name, "smoke", "fog", "dust", "blob", "mist", "steam", "splash", "particle", "cloud"))
                return AlphaBlended;
            return "Standard";
        }

        // Draws properly here: passed its test draws and isn't a copy from a mod bundle without Metal code.
        internal static bool Works(Shader s) => !Bundles.FromNoMetalBundle(s) && Proven(s);

        // The shader Unity puts on a material whose own shader is missing from its bundle.
        private static bool IsMissing(Shader s)
        {
            int id = s.GetInstanceID();
            if (!errorShaders.TryGetValue(id, out var missing)) errorShaders[id] = missing = s.name == "Hidden/InternalErrorShader";
            return missing;
        }

        private static bool Proven(Shader s)
        {
            int id = s.GetInstanceID();
            if (works.TryGetValue(id, out var ok)) return ok;
            var name = s.name;
            ok = s.isSupported && !IsMissing(s);
            if (ok && !name.StartsWith("Hidden/"))
            {
                if (DrawsPink(s)) { ok = false; Plugin.Log.LogInfo($"Shader {name} says it runs on Metal but draws pink"); }
                // Only trusted for mod copies: a wrong answer about the game's own copy would cost more than it fixes.
                else if (selfTested.Contains(name) && Bundles.FromMod(s) && SelfTest(s) == false) { ok = false; Plugin.Log.LogInfo($"Shader {name} #{id} shows its fallback shader instead of itself"); }
            }
            works[id] = ok;
            if (!ok && !IsMissing(s)) Bundles.Broken(s);
            return ok;
        }

        // Answers from test draws read back asynchronously, by shader instance id.
        private static readonly Dictionary<int, bool> pinkAhead = new Dictionary<int, bool>();
        // Test draws started per frame: each one's first use can compile a Metal pipeline.
        private const int ProbesPerFrame = 4, ProbeWaitFrames = 120;

        // Test-draws every shader the sweep could ask about and reads the pixels back with AsyncGPUReadback.
        // ReadPixels stalls until the GPU has drawn everything queued before it, which costs tens of ms per
        // shader on Apple Silicon; reading back asynchronously gives the same answer without the wait.
        private static IEnumerator ProbeAhead(Material[] materials, Slice slice)
        {
            var queue = new List<Shader>();
            var seen = new HashSet<int>();
            foreach (var mat in materials)
            {
                if (slice.Full) { yield return null; slice.Next(); }
                var s = mat != null ? mat.shader : null;
                if (s == null) continue;
                int id = s.GetInstanceID();
                if (!seen.Add(id) || works.ContainsKey(id) || pinkAhead.ContainsKey(id)) continue;
                if (Bundles.FromNoMetalBundle(s) || !s.isSupported || IsMissing(s) || s.name.StartsWith("Hidden/")) continue;
                queue.Add(s);
            }
            int pending = 0, started = 0;
            foreach (var s in queue)
            {
                if (started == ProbesPerFrame || slice.Full) { yield return null; slice.Next(); started = 0; }
                if (s == null) continue;
                int id = s.GetInstanceID();
                ProbeTime.Start();
                var mat = new Material(s);
                RenderTexture target = null;
                try
                {
                    if (mat.passCount == 0) { pinkAhead[id] = false; UnityEngine.Object.Destroy(mat); continue; }
                    target = RenderTexture.GetTemporary(4, 4, 24, RenderTextureFormat.ARGB32);
                    var previous = RenderTexture.active;
                    RenderTexture.active = target;
                    GL.Clear(true, true, Color.clear);
                    RenderTexture.active = previous;
                    Graphics.Blit(Texture2D.whiteTexture, target, mat, 0);
                    Probes++;
                    pending++;
                    started++;
                    var rt = target; var m = mat;
                    AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
                    {
                        // On an error no answer is stored, so the check falls back to the old synchronous test draw.
                        if (!request.hasError)
                        {
                            var c = request.GetData<Color32>()[2 * 4 + 2];
                            pinkAhead[id] = c.r > 229 && c.g < 26 && c.b > 229;
                        }
                        RenderTexture.ReleaseTemporary(rt);
                        UnityEngine.Object.Destroy(m);
                        pending--;
                    });
                    target = null; mat = null;
                }
                catch (Exception e) { Plugin.Log.LogError(e); }
                finally
                {
                    if (target != null) RenderTexture.ReleaseTemporary(target);
                    if (mat != null) UnityEngine.Object.Destroy(mat);
                    ProbeTime.Stop();
                }
            }
            for (int wait = 0; pending > 0 && wait < ProbeWaitFrames; wait++) yield return null;
        }

        // Draws the shader's first pass into a tiny texture. Unity draws a shader it has no Metal code
        // for with its magenta error shader, so a magenta pixel means the shader is broken here.
        private static bool DrawsPink(Shader s)
        {
            if (pinkAhead.TryGetValue(s.GetInstanceID(), out var ahead)) return ahead;
            if (probeTarget == null)
            {
                probeTarget = new RenderTexture(4, 4, 24, RenderTextureFormat.ARGB32);
                probePixels = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            }
            ProbeTime.Start();
            Probes++;
            var mat = new Material(s);
            var previous = RenderTexture.active;
            try
            {
                if (mat.passCount == 0) return false;
                RenderTexture.active = probeTarget;
                GL.Clear(true, true, Color.clear);
                Graphics.Blit(Texture2D.whiteTexture, probeTarget, mat, 0);
                RenderTexture.active = probeTarget;
                probePixels.ReadPixels(new Rect(0, 0, 4, 4), 0, 0, false);
                var c = probePixels.GetPixel(2, 2);
                return c.r > 0.9f && c.g < 0.1f && c.b > 0.9f;
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(mat);
                ProbeTime.Stop();
            }
        }

        // False when this copy draws a see-through material as solid, which the game's copy never does.
        private static bool? SelfTest(Shader s)
        {
            var mat = new Material(s);
            try
            {
                mat.EnableKeyword("_ALPHABLEND_ON");
                SetFloat(mat, "_Mode", 2);
                SetFloat(mat, "_SrcBlend", 5);
                SetFloat(mat, "_DstBlend", 10);
                SetFloat(mat, "_ZWrite", 0);
                return SeesThrough(mat);
            }
            finally { UnityEngine.Object.Destroy(mat); }
        }

        // Whether the shader, set up like this material, really lets the background show through where the
        // texture is transparent. Cached per shader and setup. When the test can't tell, assume it can.
        private static bool HandlesAlpha(Shader shader, Material mat, Setup original)
        {
            var key = $"{shader.GetInstanceID()}|{original.Mode}|{original.Src}|{original.Dst}|{original.HadBlendProps}|{string.Join(" ", Declared(shader, original.Keywords).OrderBy(k => k))}";
            if (alphaTests.TryGetValue(key, out var ok)) return ok;
            var test = new Material(mat);
            try
            {
                test.shader = shader;
                foreach (var keyword in original.Keywords) test.EnableKeyword(keyword);
                Configure(test, original);
                ok = SeesThrough(test) ?? true;
            }
            finally { UnityEngine.Object.Destroy(test); }
            alphaTests[key] = ok;
            if (!ok) Plugin.Detail($"Shader {shader.name} has no see-through variant for {mat.name}");
            return ok;
        }

        // Only keywords the shader declares change how it draws, so the others mustn't split the cache.
        private static string[] Declared(Shader shader, string[] keywords)
        {
            try
            {
                var space = shader.keywordSpace;
                return keywords.Where(k => space.FindKeyword(k).isValid).ToArray();
            }
            catch (Exception) { return keywords; }
        }

        // Draws the material over a coloured backdrop with a fully transparent texture: the backdrop must
        // survive. A second draw with an opaque texture must cover it, to prove anything was drawn at all.
        // Changes the material's colours and textures, so only call it on a throwaway copy.
        private static bool? SeesThrough(Material mat)
        {
            AlphaTime.Start();
            AlphaProbes++;
            try
            {
                int pass = MainPass(mat);
                if (pass < 0) return null;
                if (alphaTarget == null)
                {
                    alphaTarget = new RenderTexture(8, 8, 24, RenderTextureFormat.ARGB32);
                    alphaPixels = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                    clearTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    clearTexture.SetPixels(Enumerable.Repeat(new Color(1, 1, 1, 0), 16).ToArray());
                    clearTexture.Apply();
                }
                if (backdrop == null) backdrop = DrawAndRead(null, 0);
                if (backdrop == null) return null;
                Prepare(mat, true);
                var through = DrawAndRead(mat, pass);
                Prepare(mat, false);
                var solid = DrawAndRead(mat, pass);
                if (through == null || solid == null || Near(solid.Value, backdrop.Value)) return null;
                return Near(through.Value, backdrop.Value);
            }
            catch (Exception e) { Plugin.Log.LogWarning("See-through test failed: " + e.Message); return null; }
            finally { AlphaTime.Stop(); }
        }

        private static void Prepare(Material mat, bool transparent)
        {
            var colour = transparent ? new Color(1, 1, 1, 0) : Color.white;
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", transparent ? clearTexture : Texture2D.whiteTexture);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", colour);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", colour);
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
            if (mat.HasProperty("_SpecColor")) mat.SetColor("_SpecColor", Color.black);
            SetFloat(mat, "_Glossiness", 0);
            SetFloat(mat, "_GlossMapScale", 0);
            SetFloat(mat, "_Metallic", 0);
        }

        // Clears the test target to the backdrop colour, draws the material on a quad facing both ways
        // (so culling can't hide it), and reads the middle pixel back. Null when the pass can't be drawn.
        private static Color? DrawAndRead(Material mat, int pass)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = alphaTarget;
                GL.Clear(true, true, new Color(0.25f, 0.75f, 0.25f, 1f));
                if (mat != null)
                {
                    GL.PushMatrix();
                    GL.LoadOrtho();
                    var ok = mat.SetPass(pass);
                    if (ok) Graphics.DrawMeshNow(Quad(), Matrix4x4.identity);
                    GL.PopMatrix();
                    if (!ok) return null;
                }
                alphaPixels.ReadPixels(new Rect(0, 0, 8, 8), 0, 0, false);
                return alphaPixels.GetPixel(4, 4);
            }
            finally { RenderTexture.active = previous; }
        }

        private static bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.08f;

        private static Mesh Quad()
        {
            if (quad != null) return quad;
            quad = new Mesh
            {
                vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) },
                uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) },
                normals = Enumerable.Repeat(new Vector3(0, 0, -1), 4).ToArray(),
                tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 4).ToArray(),
                colors = Enumerable.Repeat(Color.white, 4).ToArray(),
                triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 },
            };
            return quad;
        }

        // The pass that draws the material in forward rendering: the ForwardBase pass, or else the last
        // pass without a lighting mode (particle shaders put a grab pass first). -1 when there is none.
        private static int MainPass(Material mat)
        {
            int found = -1;
            for (int i = 0; i < mat.passCount; i++)
            {
                var name = (mat.GetPassName(i) ?? "").ToUpperInvariant();
                if (new[] { "SHADOW", "META", "SCENE", "PICKING", "DEFERRED", "DELTA", "MOTION", "DEPTH" }.Any(name.Contains)) continue;
                string mode;
                try { mode = (mat.shader.FindPassTagValue(i, new ShaderTagId("LightMode")).name ?? "").ToUpperInvariant(); }
                catch (Exception) { mode = ""; }
                if (mode == "FORWARDBASE") return i;
                if (mode == "" || mode == "ALWAYS" || mode == "SRPDEFAULTUNLIT") found = i;
            }
            return found;
        }

        // A working shader with this name, preferring the game's own copy over a mod's.
        private static Shader Find(string name)
        {
            var s = Pick(name);
            if (s == null && Time.realtimeSinceStartup - lastRefresh > 5f) { Refresh(); s = Pick(name); }
            return s;
        }

        private static Shader FindAny(params string[] names)
        {
            foreach (var name in names)
            {
                var s = Find(name);
                if (s != null) return s;
            }
            return null;
        }

        private static Shader Pick(string name)
        {
            if (byName == null || !byName.TryGetValue(name, out var list)) return null;
            return list.FirstOrDefault(s => s != null && !Bundles.FromMod(s) && Works(s)) ?? list.FirstOrDefault(s => s != null && Works(s));
        }

        private static void Refresh()
        {
            lastRefresh = Time.realtimeSinceStartup;
            byName = new Dictionary<string, List<Shader>>();
            foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (s == null) continue;
                if (!byName.TryGetValue(s.name, out var list)) byName[s.name] = list = new List<Shader>();
                list.Add(s);
            }
        }

        // Writes every shader in use and whether it really draws, then anything on screen that is
        // still broken, for troubleshooting.
        internal static void Audit(string path)
        {
            var users = new Dictionary<Shader, List<string>>();
            foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (mat == null || mat.shader == null) continue;
                if (!users.TryGetValue(mat.shader, out var list)) users[mat.shader] = list = new List<string>();
                list.Add(mat.name);
            }
            var sb = new StringBuilder();
            sb.AppendLine(Bundles.Describe());
            foreach (var pair in users.OrderBy(p => p.Key.name))
            {
                var s = pair.Key;
                var origin = Bundles.FromMod(s) ? "mod" : "game";
                sb.AppendLine($"{s.name}\t#{s.GetInstanceID()}\t{origin}\tisSupported={s.isSupported}\t{State(s)}\t{pair.Value.Count} materials: {string.Join(", ", pair.Value.Take(6))}");
            }
            sb.AppendLine();
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                foreach (var mat in r.sharedMaterials)
                    if (mat != null && mat.shader != null && State(mat.shader) != "works")
                        sb.AppendLine($"ON SCREEN\t{PathOf(r.transform)}\t{r.GetType().Name}\t{mat.name}\t{mat.shader.name}\t{State(mat.shader)}");
            File.WriteAllText(path, sb.ToString());
        }

        // What the test draws found for a shader, without running any.
        internal static string State(Shader s)
        {
            if (IsMissing(s)) return "MISSING";
            if (!works.TryGetValue(s.GetInstanceID(), out var ok)) return "untested";
            return !ok ? "BROKEN" : Bundles.FromNoMetalBundle(s) ? "BROKEN (mod copy, no Metal)" : "works";
        }

        private static string PathOf(Transform t)
        {
            var path = t.name;
            while ((t = t.parent) != null) path = t.name + "/" + path;
            return path;
        }
    }
}

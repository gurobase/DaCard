using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DaCard.Client
{
    internal static class LayerMaps
    {
        public const string Albedo = "albedo", Normal = "normal", Roughness = "roughness", Metallic = "metallic", Mask = "mask", FoilMask = "foilmask";
    }

    internal static class CardLayers
    {
        public const string CompositeObject = "layer_composite";
        public const string LayerFoil = "_LayerFoil", LayerNormal = "_LayerNormal", BackFoil = "_BackFoil", BackNormal = "_BackNormal";

        private const float ReleaseAfterSeconds = 30f, CrowdedReleaseSeconds = 5f;
        private const int MaxLive = 32;
        // Stacks at 1.5x the card's 490 x 684: layers are placed, scaled and turned on it
        private const int StackWidth = 735, StackHeight = 1026;
        private const int ColorPass = 0, SurfacePass = 1, NormalPass = 2;
        private const float FallbackRoughness = 0.3f;
        private static readonly int RoughnessId = Shader.PropertyToID("_Roughness");
        private static readonly Color NoColor = new Color(0, 0, 0, 0), Flat = new Color(0.5f, 0.5f, 1f, 1f);

        internal class Stack
        {
            public string Key;
            public CardManifestEntry Card;
            public List<int> Front, Back;
            public Dictionary<string, int> Foils = new Dictionary<string, int>();
            public int PictureFoil;
            public float Roughness = FallbackRoughness;
            public RenderTexture FrontColor, FrontFoil, FrontNormal, BackColor, BackFoil, BackNormal;
            public readonly List<GameObject> Models = new List<GameObject>();
            public readonly Dictionary<string, RenderTexture> Texts = new Dictionary<string, RenderTexture>();
            public float LastSeen;
            public bool Dirty = true;
            // Idle: its textures' GPU memory is freed (the objects stay: the game may still draw a model that uses them)
            public bool Idle;
            public bool AnyTextureCreated() => (FrontColor != null && FrontColor.IsCreated()) || (FrontFoil != null && FrontFoil.IsCreated()) || (FrontNormal != null && FrontNormal.IsCreated()) || (BackColor != null && BackColor.IsCreated()) || (BackFoil != null && BackFoil.IsCreated()) || (BackNormal != null && BackNormal.IsCreated());
        }

        private static readonly Dictionary<string, Stack> Stacks = new Dictionary<string, Stack>();
        private static Material _composite;
        private static bool _warned;
        private static float _nextPurge;

        public static bool IsCompositeHolder(Renderer renderer) => renderer.name == CompositeObject;

        private static bool IsLive(Stack stack) => !stack.Idle && stack.FrontColor != null;

        // Every card prefab carries the stacking shader on a disabled renderer
        public static void TakeCompositeShader(GameObject model)
        {
            var holder = model.GetComponentsInChildren<Renderer>(true).FirstOrDefault(IsCompositeHolder);
            if (holder == null)
                return;
            holder.enabled = false;
            var shader = holder.sharedMaterial != null ? holder.sharedMaterial.shader : null;
            if (shader == null || !shader.isSupported)
                return;
            if (_composite == null)
                _composite = new Material(shader) { name = "dacard_layer_composite", hideFlags = HideFlags.DontUnloadUnusedAsset };
            else if (_composite.shader == null || _composite.shader.name != shader.name || !_composite.shader.isSupported)
            {
                _composite.shader = shader;
            }
        }

        public static string TrackKey(string side, int index, string map) => side + ":" + index + ":" + map;

        public static string Variant(CardManifestEntry card, CardStamp stamp, out List<int> front, out List<int> back, out bool foil)
        {
            front = Shown(card.Front, stamp);
            back = Shown(card.Back, stamp);
            foil = stamp != null && stamp.Foils.Count > 0;
            return card.Tpl + "|" + string.Join(",", front) + "|" + string.Join(",", back) + "|" + CardCopies.FoilKey(stamp);
        }

        private static List<int> Shown(List<CardLayer> layers, CardStamp stamp)
        {
            var shown = new List<int>();
            for (var i = 0; i < (layers?.Count ?? 0); i++)
                if (CardCopies.Shows(layers[i], stamp))
                    shown.Add(i);
            return shown;
        }

        // FNV-1a: stable across runs and machines (string.GetHashCode isn't)
        internal static uint Hash(string s)
        {
            var h = 2166136261;
            foreach (var c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h;
        }

        public static Stack Get(CardManifestEntry card, CardStamp stamp, List<int> front, List<int> back, string key, GameObject model)
        {
            if (_composite == null)
            {
                if (!_warned)
                    Plugin.Log.LogError("The card layer shader is missing from the card bundle (Hidden/DaCard/Layer Composite): cards show no layers. Rebuild the bundles.");
                _warned = true;
                return null;
            }

            if (!Stacks.TryGetValue(key, out var stack))
            {
                Stacks[key] = stack = new Stack { Key = key, Card = card, Front = front, Back = back, Roughness = CardRoughness(model) };
                if (stamp != null)
                    foreach (var foil in stamp.Foils)
                        stack.Foils[foil.Key] = FoilTypes.Index(foil.Value);
                stack.PictureFoil = stack.Foils.TryGetValue(FoilTypes.Picture, out var picture) ? picture : 0;
            }
            if (!stack.Models.Contains(model))
                stack.Models.Add(model);
            stack.LastSeen = Time.time;
            if (stack.FrontColor == null)
                Allocate(stack);
            stack.Idle = false;
            Restore(stack);
            if (stack.Dirty)
                Build(stack);
            return stack;
        }

        public static void Apply(Material material, Stack stack, string colorProperty, string backProperty)
        {
            if (stack == null)
                return;
            material.SetTexture(colorProperty, stack.FrontColor);
            material.SetTexture(LayerFoil, stack.FrontFoil);
            material.SetTexture(LayerNormal, stack.FrontNormal);
            material.SetTexture(backProperty, stack.BackColor);
            material.SetTexture(BackFoil, stack.BackFoil);
            material.SetTexture(BackNormal, stack.BackNormal);
        }

        // An animated layer showed a new frame
        public static void MarkDirty(string templateId)
        {
            foreach (var stack in Stacks.Values)
                if (stack.Card.Tpl == templateId)
                    stack.Dirty = true;
        }

        public static void Tick()
        {
            foreach (var stack in Stacks.Values)
            {
                stack.Models.RemoveAll(m => m == null);
                var shown = stack.Models.Any(m => m.activeInHierarchy);
                if (shown)
                    stack.LastSeen = Time.time;
                if (stack.FrontColor == null)
                    continue;
                if (stack.Idle)
                {
                    // Shown again, or something drew with it (Unity recreates a released texture when it's used): stack it again
                    if (!shown && !stack.AnyTextureCreated())
                        continue;
                    stack.Idle = false;
                    stack.LastSeen = Time.time;
                    stack.Dirty = true;
                    Plugin.Log.LogInfo($"Card layers {stack.Key}: in use again, stacked again");
                    shown = true;
                }
                Restore(stack);
                if (stack.Dirty && shown)
                    Build(stack);
            }

            if (Time.time < _nextPurge)
                return;
            _nextPurge = Time.time + 45f;
            // Unused for a while: free the GPU memory. Never destroy the textures or materials: the game can copy or pool
            // a card model without us seeing it. First check that no shown renderer still draws with its textures:
            // by texture, as the game draws with copies of our materials (the inspect preview does), which still use them.
            var live = Stacks.Values.Where(IsLive).OrderBy(s => s.LastSeen).ToList();
            var excess = live.Count - MaxLive;
            var idle = live.Where((s, i) => Time.time - s.LastSeen > (i < excess ? CrowdedReleaseSeconds : ReleaseAfterSeconds)).ToList();
            var freed = Stacks.Values.Where(s => s.Idle && s.AnyTextureCreated()).ToList();

            if (idle.Count == 0 && freed.Count == 0)
                return;

            var candidates = new HashSet<Texture>();
            foreach (var stack in idle.Concat(freed))
            {
                if (stack.FrontFoil != null) candidates.Add(stack.FrontFoil);
                if (stack.BackFoil != null) candidates.Add(stack.BackFoil);
            }

            var used = TexturesInUse(candidates);

            foreach (var stack in freed)
            {
                if (!used.Contains(stack.FrontFoil) && !used.Contains(stack.BackFoil)) continue;

                stack.Idle = false;
                stack.LastSeen = Time.time;
                stack.Dirty = true;
                Restore(stack);
                Build(stack);
                Plugin.Log.LogInfo($"Card layers {stack.Key}: still drawn, stacked again");
            }

            foreach (var stack in idle)
            {
                if (used.Contains(stack.FrontFoil) || used.Contains(stack.BackFoil))
                {
                    stack.LastSeen = Time.time;
                    continue;
                }

                ReleaseTextures(stack);

                stack.Idle = true;
                Plugin.Log.LogInfo($"Card layers {stack.Key}: unused, GPU memory freed");
            }
        }

        private static readonly List<Material> SharedMaterials = new List<Material>();

        private static readonly int LayerFoilId = Shader.PropertyToID(LayerFoil), BackFoilId = Shader.PropertyToID(BackFoil);

        // The layer stack textures that some card material (ours or a copy the game made of it) still draws with
        private static HashSet<Texture> TexturesInUse(HashSet<Texture> candidates)
        {
            var used = new HashSet<Texture>();
            if (candidates.Count == 0) return used;

            foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || !renderer.gameObject.activeInHierarchy)
                    continue;

                renderer.GetSharedMaterials(SharedMaterials);
                foreach (var material in SharedMaterials)
                {
                    if (material == null || !material.HasProperty(LayerFoilId))
                        continue;

                    var texture = material.GetTexture(LayerFoilId);
                    if (texture != null && candidates.Contains(texture))
                        used.Add(texture);

                    if (material.HasProperty(BackFoilId))
                    {
                        texture = material.GetTexture(BackFoilId);
                        if (texture != null && candidates.Contains(texture))
                            used.Add(texture);
                    }
                    if (used.Count == candidates.Count)
                        return used;
                }
            }
            return used;
        }

        private static float CardRoughness(GameObject model)
        {
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if (IsCompositeHolder(renderer))
                    continue;
                renderer.GetSharedMaterials(SharedMaterials);
                foreach (var material in SharedMaterials)
                    if (material != null && material.HasProperty(RoughnessId))
                        return material.GetFloat(RoughnessId);
            }
            return FallbackRoughness;
        }

        private static void Restore(Stack stack)
        {
            RestoreTexture(stack.FrontColor, stack);
            RestoreTexture(stack.FrontFoil, stack);
            RestoreTexture(stack.FrontNormal, stack);
            RestoreTexture(stack.BackColor, stack);
            RestoreTexture(stack.BackFoil, stack);
            RestoreTexture(stack.BackNormal, stack);
        }

        private static void RestoreTexture(RenderTexture texture, Stack stack)
        {
            if (texture != null && !texture.IsCreated())
            {
                texture.Create();
                stack.Dirty = true;
            }
        }

        private static void ReleaseTextures(Stack stack)
        {
            stack.FrontColor?.Release();
            stack.FrontFoil?.Release();
            stack.FrontNormal?.Release();
            stack.BackColor?.Release();
            stack.BackFoil?.Release();
            stack.BackNormal?.Release();

            foreach (var texture in stack.Texts.Values)
                texture?.Release();
        }

        private static void Allocate(Stack stack)
        {
            stack.Dirty = true;
            stack.FrontColor = NewTexture(stack.Key + " front", linear: false);
            stack.FrontFoil = NewTexture(stack.Key + " front foil", linear: true);
            stack.FrontNormal = NewTexture(stack.Key + " front normal", linear: true);
            stack.BackColor = NewTexture(stack.Key + " back", linear: false);
            stack.BackFoil = NewTexture(stack.Key + " back foil", linear: true);
            stack.BackNormal = NewTexture(stack.Key + " back normal", linear: true);
        }

        // The card UV -> layer UV map of a layer's transform (see CardLayerComposite.shader): u' = U.x u + U.y v + U.z
        private static (Vector4 U, Vector4 V, Vector4 Rot) Placement(LayerTransform t, Texture art)
        {
            t = t ?? new LayerTransform();
            float w = StackWidth, h = StackHeight, iw = Mathf.Max(1, art.width), ih = Mathf.Max(1, art.height);
            var scale = Mathf.Min(w / iw, h / ih) * Mathf.Max(1e-4f, t.Scale);
            var rad = t.Rotation * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            Vector2 Map(float u, float v)
            {
                // card pixels (y down) around the layer's centre, turned back, in the picture's pixels (y down)
                float dx = u * w - t.X * w, dy = (1 - v) * h - t.Y * h;
                float qx = (c * dx + s * dy) / scale + iw / 2, qy = (-s * dx + c * dy) / scale + ih / 2;
                return new Vector2(qx / iw, 1 - qy / ih);
            }
            var o = Map(0, 0);
            var du = Map(1, 0) - o;
            var dv = Map(0, 1) - o;
            return (new Vector4(du.x, dv.x, o.x, 0), new Vector4(du.y, dv.y, o.y, 0), new Vector4(c, s, 0, 0));
        }

        private static RenderTexture NewTexture(string name, bool linear)
        {
            var texture = new RenderTexture(StackWidth, StackHeight, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB)
            {
                name = "dacard " + name,
                useMipMap = true,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            texture.Create();
            return texture;
        }

        private static void Build(Stack stack)
        {
            stack.Dirty = false;
            var active = RenderTexture.active;
            try
            {
                BuildSide(stack, stack.Card.Front, stack.Front, "front", stack.FrontColor, stack.FrontFoil, stack.FrontNormal);
                BuildSide(stack, stack.Card.Back, stack.Back, "back", stack.BackColor, stack.BackFoil, stack.BackNormal);
            }
            finally
            {
                RenderTexture.active = active;
            }
        }

        private static void BuildSide(Stack stack, List<CardLayer> layers, List<int> shown, string side,
            RenderTexture color, RenderTexture surface, RenderTexture normal)
        {
            var card = stack.Card;
            var parts = new List<Part>();
            foreach (var i in shown)
            {
                if (layers[i].Text != null)
                {
                    var key = side + ":" + i;
                    stack.Texts.TryGetValue(key, out var text);
                    if (text == null || !text.IsCreated())
                        stack.Texts[key] = text = TextLayers.Render(card, layers[i], StackWidth, StackHeight, text);
                    if (text != null)
                        parts.Add(new Part { Layer = layers[i], Albedo = text, Foil = FoilOf(stack, layers[i]) });
                    continue;
                }
                var albedo = Current(card, layers, i, side, LayerMaps.Albedo);
                var mask = Current(card, layers, i, side, LayerMaps.Mask);
                if (albedo != null || mask != null)
                    parts.Add(new Part
                    {
                        Layer = layers[i],
                        Albedo = albedo,
                        Foil = FoilOf(stack, layers[i]),
                        Mask = mask,
                        FoilMask = Current(card, layers, i, side, LayerMaps.FoilMask),
                        Normal = Current(card, layers, i, side, LayerMaps.Normal),
                        Roughness = Current(card, layers, i, side, LayerMaps.Roughness) ?? Solid(layers[i].Roughness),
                        Metallic = Current(card, layers, i, side, LayerMaps.Metallic) ?? Solid(layers[i].Metallic)
                    });
            }

            _composite.SetFloat("_DefaultRoughness", stack.Roughness);
            Composite(color, ColorPass, NoColor, parts);
            Composite(surface, SurfacePass, new Color(0, 0, stack.Roughness, 0), parts);
            Composite(normal, NormalPass, new Color(Flat.r, Flat.g, Flat.b, stack.PictureFoil / 255f), parts);
        }

        private static readonly Dictionary<int, Texture2D> SolidTextures = new Dictionary<int, Texture2D>();

        private static Texture Solid(float? value)
        {
            if (value == null)
                return null;
            var level = Mathf.RoundToInt(Mathf.Clamp01(value.Value) * 255f);
            if (SolidTextures.TryGetValue(level, out var texture) && texture != null)
                return texture;
            texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
            {
                name = "dacard solid " + level,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            var b = (byte)level;
            texture.SetPixels32(new[] { new Color32(b, b, b, 255) });
            texture.Apply(false, true);
            SolidTextures[level] = texture;
            return texture;
        }

        private class Part
        {
            public CardLayer Layer;
            public Texture Albedo, Mask, FoilMask, Normal, Roughness, Metallic;
            public int Foil = -1;
        }

        private static int FoilOf(Stack stack, CardLayer layer) =>
            layer.Layer != null && stack.Foils.TryGetValue(layer.Layer, out var type) ? type : -1;

        private static Texture2D _clear;

        private static Texture2D Clear
        {
            get
            {
                if (_clear != null)
                    return _clear;
                _clear = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "dacard layer clear", hideFlags = HideFlags.DontUnloadUnusedAsset };
                _clear.SetPixel(0, 0, new Color(0, 0, 0, 0));
                _clear.Apply(false, true);
                return _clear;
            }
        }

        private static void SetMap(string property, string flag, Texture texture)
        {
            _composite.SetTexture(property, texture);
            _composite.SetFloat(flag, texture != null ? 1f : 0f);
        }

        // One blit per layer, ping-ponging between the target and a scratch texture
        private static void Composite(RenderTexture target, int pass, Color empty, List<Part> parts)
        {
            var descriptor = target.descriptor;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            var scratch = RenderTexture.GetTemporary(descriptor);
            try
            {
                RenderTexture.active = target;
                GL.Clear(false, true, empty);
                var current = target;
                foreach (var part in parts)
                {
                    var layer = part.Layer;
                    var next = current == target ? scratch : target;
                    _composite.SetTexture("_Layer", part.Albedo != null ? part.Albedo : Clear);
                    _composite.SetFloat("_HasAlbedo", part.Albedo != null ? 1f : 0f);
                    SetMap("_MaskMap", "_HasMask", part.Mask);
                    SetMap("_FoilMaskMap", "_HasFoilMask", part.FoilMask);
                    SetMap("_NormalMap", "_HasNormal", part.Normal);
                    SetMap("_RoughnessMap", "_HasRoughness", part.Roughness);
                    SetMap("_MetallicMap", "_HasMetallic", part.Metallic);
                    _composite.SetFloat("_CanBeFoil", part.Foil >= 0 ? 1f : 0f);
                    _composite.SetFloat("_FoilType", Mathf.Max(0, part.Foil));
                    _composite.SetFloat("_Frame", layer.Frame ? 1f : 0f);
                    _composite.SetFloat("_Premultiplied", layer.Text != null ? 1f : 0f);
                    var (u, v, rot) = Placement(layer.Transform, part.Albedo != null ? part.Albedo : part.Mask);
                    _composite.SetVector("_LayerU", u);
                    _composite.SetVector("_LayerV", v);
                    _composite.SetVector("_NormalRot", rot);
                    Graphics.Blit(current, next, _composite, pass);
                    current = next;
                }
                if (current != target)
                    Graphics.Blit(current, target);
                target.GenerateMips();
            }
            finally
            {
                RenderTexture.ReleaseTemporary(scratch);
            }
        }

        // The layer map now: an animated map's current frame, else its picture
        private static Texture Current(CardManifestEntry card, List<CardLayer> layers, int index, string side, string map)
        {
            var layer = layers[index];
            var frame = CardAnimator.Frame(card.Tpl, TrackKey(side, index, map));
            if (frame != null)
                return frame;

            if (layer.Textures != null && layer.Textures.TryGetValue(map, out var url) && url != null)
            {
                var texture = CardRegistry.GetTexture(url, linear: map != LayerMaps.Albedo);
                return texture;
            }
            return null;
        }
    }
}

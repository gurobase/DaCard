using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SPT.Common.Http;
using TMPro;
using UnityEngine;

namespace DaCard.Client
{
    internal static class CardRegistry
    {

        private static readonly Dictionary<string, CardManifestEntry> Cards = new Dictionary<string, CardManifestEntry>();
        private static readonly Dictionary<string, BinderManifestEntry> Binders = new Dictionary<string, BinderManifestEntry>();
        private static readonly Dictionary<string, CardTemplate> Templates = new Dictionary<string, CardTemplate>();

        private const string StickerObject = "sticker";
        private static readonly Dictionary<string, string> Versions = new Dictionary<string, string>();
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Task<Texture2D>> Loading = new Dictionary<string, Task<Texture2D>>();
        private static readonly Dictionary<string, Task<CardManifestEntry>> Fetching = new Dictionary<string, Task<CardManifestEntry>>();

        private static readonly Dictionary<(Material, string), Material> Materials = new Dictionary<(Material, string), Material>();
        private static readonly Dictionary<Material, Material> CopyToBase = new Dictionary<Material, Material>();

        private static readonly HashSet<int> CardModels = new HashSet<int>();

        private static string _backProperty = "_CARD_BACK";
        private static string _overlayProperty = "_CARD_FRONT_BORDER";
        private static List<TextureSlot> _slots = new List<TextureSlot>();

        private static readonly HashSet<string> CardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static int Count => CardIds.Count;

        private static readonly SemaphoreSlim Decoders = new SemaphoreSlim(2);

        public static void Load()
        {
            ClientIndex index;
            try
            {
                index = JsonConvert.DeserializeObject<ClientIndex>(RequestHandler.GetJson("/dacard/index"));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not get the card list from the server (is the DaCard server mod installed?): " + e.Message);
                return;
            }

            if (index?.Versions == null)
                return;
            CardCache.Init();

            if (!string.IsNullOrEmpty(index.BackProperty))
                _backProperty = index.BackProperty;
            if (!string.IsNullOrEmpty(index.OverlayProperty))
                _overlayProperty = index.OverlayProperty;
            _slots = index.Slots ?? new List<TextureSlot>();
            foreach (var id in index.Versions.Keys)
                CardIds.Add(id);
            foreach (var pair in index.Versions ?? new Dictionary<string, string>())
                Versions[pair.Key] = pair.Value;
            foreach (var pair in index.Templates ?? new Dictionary<string, CardTemplate>())
                Templates[pair.Key] = pair.Value;
            foreach (var binder in index.Binders ?? new List<BinderManifestEntry>())
                Binders[binder.Tpl] = binder;
            PackRegistry.Load(index);
            CardCache.Sync(index);
        }

        public static bool IsLinear(string property) => _slots.FirstOrDefault(s => s.Property == property)?.Linear ?? false;

        public static bool IsCard(string templateId) => templateId != null && Templates.ContainsKey(templateId);

        public static CardTemplate Template(string templateId) => templateId != null && Templates.TryGetValue(templateId, out var template) ? template : null;

        public static bool IsCardId(string cardId) => cardId != null && CardIds.Contains(cardId);

        public static CardManifestEntry Card(string cardId)
        {
            if (!IsCardId(cardId))
                return null;
            if (Cards.TryGetValue(cardId, out var cached))
                return cached;
            if (Fetching.ContainsKey(cardId))
                return null;
            var card = CardCache.ReadCard(cardId, VersionOf(cardId));
            if (card != null)
                Cards[cardId] = card;
            else
                CardAsync(cardId);
            return card;
        }

        private static string VersionOf(string cardId) => Versions.TryGetValue(cardId, out var v) ? v : null;

        private static async Task<CardManifestEntry> Download(string cardId)
        {
            var version = VersionOf(cardId);
            var card = await Task.Run(() => CardCache.ReadCard(cardId, version));
            if (card != null)
                return card;
            try
            {
                var json = await RequestHandler.GetJsonAsync("/dacard/card/" + cardId);
                card = JsonConvert.DeserializeObject<CardManifestEntry>(json);
                if (card != null)
                    CardCache.WriteCard(cardId, version, json);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not get card {cardId} from the server: {e.Message}");
            }
            return card;
        }

        public static Task<CardManifestEntry> CardAsync(string cardId)
        {
            if (!IsCardId(cardId))
                return Task.FromResult<CardManifestEntry>(null);
            if (Cards.TryGetValue(cardId, out var cached))
                return Task.FromResult(cached);
            if (Fetching.TryGetValue(cardId, out var pending))
                return pending;
            var task = FetchCard(cardId);
            if (!task.IsCompleted)
                Fetching[cardId] = task;
            return task;
        }

        private static async Task<CardManifestEntry> FetchCard(string cardId)
        {
            CardManifestEntry card;
            try
            {
                card = await Download(cardId);
            }
            finally
            {
                Fetching.Remove(cardId);
            }
            if (Cards.TryGetValue(cardId, out var existing))
                return existing;
            Cards[cardId] = card;
            return card;
        }

        public static Task<Texture2D> TextureAsync(string url, bool linear)
        {
            if (url == null)
                return Task.FromResult<Texture2D>(null);
            if (Textures.TryGetValue(url, out var texture) && texture != null)
                return Task.FromResult(texture);
            if (Loading.TryGetValue(url, out var pending))
                return pending;
            var task = LoadTexture(url, linear);
            if (!task.IsCompleted)
                Loading[url] = task;
            return task;
        }

        private static async Task<Texture2D> LoadTexture(string url, bool linear)
        {
            byte[] png = null;
            PngDecoder.Image image = null;
            try
            {
                await Task.Run(async () =>
                {
                    png = await CardCache.DataAsync(url);
                    if (png == null || png.Length == 0)
                        return;
                    await Decoders.WaitAsync();
                    try
                    {
                        image = PngDecoder.Decode(png);
                    }
                    finally
                    {
                        Decoders.Release();
                    }
                });
                await FrameBudget.Turn();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not load {url}: {e.Message}");
            }
            finally
            {
                Loading.Remove(url);
            }
            if (Textures.TryGetValue(url, out var existing) && existing != null)
                return existing;
            var start = FrameBudget.Start();
            var texture = image != null || (png != null && png.Length > 0) ? CardTextures.Create(image, image != null ? null : png, url, linear) : null;
            FrameBudget.Spend(start);
            if (texture == null)
            {
                Plugin.Log.LogError("No image data for " + url);
                CardCache.Forget(url);
                return null;
            }
            Textures[url] = texture;
            return texture;
        }

        public static async Task PrepareCard(CardStamp stamp)
        {
            var card = await CardAsync(stamp?.Card);
            if (card == null)
                return;
            var loads = new List<Task>();
            foreach (var slot in _slots)
                if (card.Textures != null && card.Textures.TryGetValue(slot.Property, out var url))
                    loads.Add(TextureAsync(url, slot.Linear));
            foreach (var layer in (card.Front ?? new List<CardLayer>()).Concat(card.Back ?? new List<CardLayer>()))
                if (CardCopies.Shows(layer, stamp) && layer.Textures != null)
                    foreach (var pair in layer.Textures)
                        loads.Add(TextureAsync(pair.Value, pair.Key != LayerMaps.Albedo));
            await Task.WhenAll(loads);
        }

        public static Task PrepareBinder(string templateId) =>
            Binders.TryGetValue(templateId, out var binder)
                ? Task.WhenAll((binder.Stickers ?? new List<BinderSticker>()).Where(s => s?.Image != null).Select(s => TextureAsync(s.Image, false)))
                : Task.CompletedTask;

        public static string PocketCard(Slot slot)
        {
            var binder = slot?.ParentItem?.StringTemplateId;
            return binder != null && slot.Name != null && Binders.TryGetValue(binder, out var entry) && entry.Pockets != null
                   && entry.Pockets.TryGetValue(slot.Name, out var card) ? card : null;
        }

        public static bool IsBinder(string templateId) => templateId != null && Binders.ContainsKey(templateId);

        private static readonly Dictionary<string, Sprite> SlotSprites = new Dictionary<string, Sprite>();

        public static Sprite EmptySlotSprite() => SlotSprite("card_slot");

        private static Sprite SlotSprite(string name)
        {
            if (SlotSprites.TryGetValue(name, out var cached) && cached != null)
                return cached;
            using (var stream = typeof(CardRegistry).Assembly.GetManifestResourceStream($"DaCard.{name}.png"))
            {
                if (stream == null)
                {
                    Plugin.Log.LogError($"Embedded {name}.png is missing");
                    return null;
                }
                var bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
                texture.LoadImage(bytes, markNonReadable: true);
                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                sprite.name = name;
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                SlotSprites[name] = sprite;
                return sprite;
            }
        }

        public static void ApplyBinder(GameObject model, string templateId)
        {
            if (!Binders.TryGetValue(templateId, out var binder))
                return;

            CardModels.Add(model.GetInstanceID());
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            var template = renderers.FirstOrDefault(r => r.name == StickerObject);
            if (template == null)
                return;
            var layers = new List<Renderer> { template };
            for (var i = 1; ; i++)
            {
                var copy = renderers.FirstOrDefault(r => r.name == StickerObject + "_layer" + i);
                if (copy == null) break;
                layers.Add(copy);
            }

            var stickers = binder.Stickers ?? new List<BinderSticker>();
            for (var i = 0; i < Math.Max(stickers.Count, layers.Count); i++)
            {
                var texture = i < stickers.Count && stickers[i]?.Image != null ? GetTexture(stickers[i].Image, linear: false) : null;
                if (texture == null)
                {
                    if (i < layers.Count) layers[i].enabled = false;
                    continue;
                }
                if (i >= layers.Count)
                    layers.Add(AddLayer(template, i));
                var renderer = layers[i];
                renderer.enabled = true;

                var current = renderer.sharedMaterial;
                var bundleMaterial = CopyToBase.TryGetValue(current, out var b) && b != null ? b : current;
                var key = templateId + "#" + i;
                if (!Materials.TryGetValue((bundleMaterial, key), out var material) || material == null)
                {
                    var p = stickers[i].Placement ?? new StickerPlacement();
                    material = new Material(bundleMaterial) { name = bundleMaterial.name + "_" + templateId + "_" + i, hideFlags = HideFlags.DontUnloadUnusedAsset };
                    material.SetTexture("_MainTex", texture);
                    material.SetVector("_StickerCenter", new Vector4(p.X, 1 - p.Y, 0, 0));
                    material.SetVector("_StickerSize", new Vector4(p.Width, p.Height, 0, 0));
                    material.SetFloat("_StickerAngle", p.Rotation);
                    material.SetFloat("_OffsetUnits", -1 - 4 * i);
                    material.renderQueue = bundleMaterial.renderQueue + i;
                    Materials[(bundleMaterial, key)] = material;
                    CopyToBase[material] = bundleMaterial;
                }
                renderer.sharedMaterial = material;
            }
        }

        private static Renderer AddLayer(Renderer template, int index)
        {
            var copy = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
            copy.name = StickerObject + "_layer" + index;
            return copy.GetComponent<Renderer>();
        }

        public static bool IsCardModel(GameObject model) => model != null && CardModels.Contains(model.GetInstanceID());

        public static void Apply(GameObject model, CardStamp stamp, string itemId)
        {
            var card = Card(stamp?.Card);
            if (card == null)
                return;

            CardModels.Add(model.GetInstanceID());

            CardText.Apply(model, card, CardNames.Name(card));

            CardLayers.TakeCompositeShader(model);
            var variant = CardLayers.Variant(card, stamp, out var front, out var back, out var foil);
            var stack = CardLayers.Get(card, stamp, front, back, variant, model);

            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.GetComponent<TMP_Text>() != null || CardLayers.IsCompositeHolder(renderer) || CardGlow.IsGlow(renderer))
                    continue;

                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    var current = materials[i];
                    if (current == null || IsEdge(current))
                        continue;

                    var bundleMaterial = CopyToBase.TryGetValue(current, out var b) && b != null ? b : current;
                    materials[i] = GetMaterial(card, bundleMaterial, variant, stack, foil, stamp != null && stamp.Foils.ContainsKey(FoilTypes.Picture));
                }
                renderer.sharedMaterials = materials;
                foreach (var material in materials)
                    if (material != null && !IsEdge(material))
                        CardAnimator.Attach(card, material, model);
            }

            CardGlow.Apply(model, card, itemId);
        }

        private const string EdgeShader = "DaCard/Card Edge";

        private static bool IsEdge(Material material) => material.shader != null && material.shader.name == EdgeShader;

        // One material per card copy variant (the layers it rolled)
        private static Material GetMaterial(CardManifestEntry card, Material bundleMaterial, string variant, CardLayers.Stack stack, bool foil, bool pictureFoil)
        {
            if (Materials.TryGetValue((bundleMaterial, variant), out var material) && material != null)
                return material;

            PurgeUnloaded();

            material = new Material(bundleMaterial)
            {
                name = bundleMaterial.name + "_" + card.Tpl,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };

            // card.png and its maps: a 3D card's picture behind the window. Other cards have none: all their pictures are layers.
            var picture = _slots.FirstOrDefault(s => s.Suffix == "");
            var noPicture = picture == null || !card.Textures.ContainsKey(picture.Property);
            foreach (var slot in _slots)
            {
                var own = card.Textures.TryGetValue(slot.Property, out var url);
                var texture = own
                    ? GetTexture(url, slot.Linear)
                    : noPicture && slot.Suffix == "" ? ClearTexture
                    : noPicture && slot.Suffix == "foil" ? Texture2D.blackTexture
                    : DefaultTexture(slot.Default);
                if (slot.Suffix == "foil" && !pictureFoil)
                    texture = Texture2D.blackTexture;
                if (texture != null)
                    material.SetTexture(slot.Property, texture);
                if (!string.IsNullOrEmpty(slot.Flag))
                    material.SetFloat(slot.Flag, own && texture != null ? 1f : 0f);
            }

            if (stack != null)
            {
                CardLayers.Apply(material, stack, _overlayProperty, _backProperty);
                Plugin.Log.LogInfo($"Card {card.Tpl}: backProperty={_backProperty}, hasProperty={material.HasProperty(_backProperty)}, backTexture={material.GetTexture(_backProperty)?.name ?? "null"}");
            }
            else if (material.HasProperty(_overlayProperty))
                material.SetTexture(_overlayProperty, ClearTexture);

            if (!foil)
                material.SetFloat("_FoilStrength", 0f);
            if (card.RarityColor != null && ColorUtility.TryParseHtmlString(card.RarityColor, out var rarityColor))
                material.SetColor("_RarityColor", rarityColor);

            if (card.Floats != null)
                foreach (var pair in card.Floats)
                    material.SetFloat(pair.Key, pair.Value);

            Materials[(bundleMaterial, variant)] = material;
            CopyToBase[material] = bundleMaterial;
            return material;
        }


        private static void PurgeUnloaded()
        {
            foreach (var key in Materials.Keys.Where(k => k.Item1 == null).ToList())
            {
                var copy = Materials[key];
                Materials.Remove(key);
                if (copy != null)
                {
                    CopyToBase.Remove(copy);
                    UnityEngine.Object.Destroy(copy);
                }
            }
        }

        private static Texture2D _clearTexture;

        private static Texture2D ClearTexture
        {
            get
            {
                if (_clearTexture != null)
                    return _clearTexture;
                _clearTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "dacard_clear", hideFlags = HideFlags.DontUnloadUnusedAsset };
                _clearTexture.SetPixel(0, 0, new Color(0, 0, 0, 0));
                _clearTexture.Apply(false, true);
                return _clearTexture;
            }
        }

        private static Texture2D DefaultTexture(string name)
        {
            switch (name?.ToLowerInvariant())
            {
                case "black": return Texture2D.blackTexture;
                case "white": return Texture2D.whiteTexture;
                case "gray":
                case "grey": return Texture2D.grayTexture;
                case "bump":
                case "normal": return Texture2D.normalTexture;
                default: return null;
            }
        }

        public static byte[] ImageData(string url)
        {
            var bytes = CardCache.Data(url);
            Plugin.Log.LogInfo($"Image {url}: {bytes.Length} bytes, signature={BitConverter.ToString(bytes.Take(Math.Min(8, bytes.Length)).ToArray())}");
            if (bytes == null || bytes.Length == 0)
            {
                Plugin.Log.LogError("No image data for " + url);
                return null;
            }
            return bytes;
        }

        public static Texture2D GetTexture(string url, bool linear)
        {
            if (Textures.TryGetValue(url, out var texture) && texture != null)
                return texture;

            var bytes = ImageData(url);
            if (bytes == null)
                return null;

            texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear)
            {
                name = url,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };

            if (!texture.LoadImage(bytes, markNonReadable: true))
            {
                Plugin.Log.LogError("Not a valid PNG: " + url);
                CardCache.Forget(url);
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            Textures[url] = texture;
            return texture;
        }
    }
}

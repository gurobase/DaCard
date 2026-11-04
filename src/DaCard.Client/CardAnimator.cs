using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SPT.Common.Http;
using UnityEngine;

namespace DaCard.Client
{
    internal static class CardAnimator
    {
        private const float UnloadAfterSeconds = 60f;
        private const float StartAfterSeconds = 0.5f;
        private const int Workers = 4;
        private const int MaxDecodedAhead = 48;
        private const long UploadBudgetMs = 4;

        // One animated map with its own frame count and speed: a material texture property (a 3D card's picture), or a
        // layer's map (Layer: "front:0:art"...), which the card's layer stacks use (CardLayers)
        private class Track
        {
            public string Property;
            public string Layer;
            public CardAnimationTrack Info;
            public Texture2D[] Frames;
            public int Available;
            public int LastFrame = -1;

            public float Fps => (float)Math.Max(0.1, Info.Fps);
        }

        private class Anim
        {
            public CardManifestEntry Card;
            public readonly HashSet<Material> Materials = new HashSet<Material>();
            public readonly List<GameObject> Models = new List<GameObject>();
            public readonly Dictionary<Material, Dictionary<string, Texture>> Stills = new Dictionary<Material, Dictionary<string, Texture>>();
            public List<Track> Tracks;
            public int Generation;
            public bool Failed, Started;
            public float LastSeen;
            public double Clock;
        }

        private static readonly Dictionary<string, Anim> Anims = new Dictionary<string, Anim>();

        private static bool IsPlayable(CardAnimationTrack t) => t != null && t.Frames > 1 && !string.IsNullOrEmpty(t.Url);

        private static List<Track> Playable(CardManifestEntry card)
        {
            var tracks = (card.Animation?.Tracks ?? new Dictionary<string, CardAnimationTrack>())
                .Where(t => IsPlayable(t.Value)).Select(t => new Track { Property = t.Key, Info = t.Value }).ToList();
            foreach (var (side, layers) in new[] { ("front", card.Front), ("back", card.Back) })
                for (var i = 0; i < (layers?.Count ?? 0); i++)
                    foreach (var map in layers[i]?.Animation ?? new Dictionary<string, CardAnimationTrack>())
                        if (IsPlayable(map.Value))
                            tracks.Add(new Track { Layer = CardLayers.TrackKey(side, i, map.Key), Property = map.Key, Info = map.Value });
            return tracks;
        }

        // A layer map's frame now (null: not loaded, the layer shows its picture)
        public static Texture Frame(string templateId, string layer)
        {
            if (!Anims.TryGetValue(templateId, out var anim) || anim.Tracks == null)
                return null;
            var track = anim.Tracks.FirstOrDefault(t => t.Layer == layer);
            return track != null && track.LastFrame >= 0 ? track.Frames[track.LastFrame] : null;
        }

        public static void Attach(CardManifestEntry card, Material material, GameObject model)
        {
            var playable = Playable(card);
            if (playable.Count == 0)
                return;

            if (!Anims.TryGetValue(card.Tpl, out var anim))
                Anims[card.Tpl] = anim = new Anim { Card = card, LastSeen = Time.time };

            if (anim.Materials.Add(material))
                anim.Stills[material] = playable.Where(t => t.Layer == null).ToDictionary(t => t.Property, t => material.GetTexture(t.Property));
            if (!anim.Models.Contains(model))
                anim.Models.Add(model);

            if (anim.Tracks != null)
                foreach (var track in anim.Tracks.Where(t => t.LastFrame >= 0 && t.Layer == null))
                    material.SetTexture(track.Property, track.Frames[track.LastFrame]);
        }

        public static void Tick()
        {
            foreach (var anim in Anims.Values)
            {
                anim.Models.RemoveAll(m => m == null);
                anim.Materials.RemoveWhere(m => m == null);
                var visible = anim.Models.Any(m => m.activeInHierarchy);
                if (visible)
                    anim.LastSeen = Time.time;

                if (anim.Tracks == null)
                {
                    if (visible && !anim.Failed)
                        Plugin.Instance.StartCoroutine(Load(anim));
                    continue;
                }

                if (!visible && Time.time - anim.LastSeen > UnloadAfterSeconds)
                {
                    Unload(anim);
                    continue;
                }

                if (!anim.Started)
                {
                    if (!anim.Tracks.All(t => t.Available >= Math.Min(t.Info.Frames, Math.Max(1, Mathf.CeilToInt(t.Fps * StartAfterSeconds)))))
                        continue;
                    anim.Started = true;
                }

                anim.Clock += Time.deltaTime;
                var layersChanged = false;
                foreach (var track in anim.Tracks)
                {
                    var frame = (int)(anim.Clock * track.Fps);
                    frame = track.Available == track.Info.Frames ? frame % track.Info.Frames : Math.Min(frame, track.Available - 1);
                    if (frame == track.LastFrame)
                        continue;
                    track.LastFrame = frame;
                    if (track.Layer != null)
                        layersChanged = true;
                    else
                        foreach (var material in anim.Materials)
                            material.SetTexture(track.Property, track.Frames[frame]);
                }
                if (layersChanged)
                    CardLayers.MarkDirty(anim.Card.Tpl);
            }
        }

        private class Decoded
        {
            public PngDecoder.Image Image;
            public byte[] Png;
            public string Error;
        }

        private static IEnumerator Load(Anim anim)
        {
            var generation = anim.Generation;
            var tracks = Playable(anim.Card);
            foreach (var track in tracks)
                track.Frames = new Texture2D[track.Info.Frames];
            anim.Tracks = tracks;
            anim.Clock = 0;
            anim.Started = false;

            // In play order, so every track fills up at the pace it's shown
            var jobs = tracks.SelectMany(t => Enumerable.Range(0, t.Info.Frames).Select(i => (track: t, frame: i, url: FrameUrl(t.Info, i))))
                .OrderBy(j => j.frame / j.track.Fps)
                .ToList();

            var results = new ConcurrentDictionary<int, Decoded>();
            var ahead = new SemaphoreSlim(MaxDecodedAhead);
            var cancel = new CancellationTokenSource();
            var next = -1;
            for (var w = 0; w < Workers; w++)
            {
                Task.Run(async () =>
                {
                    while (true)
                    {
                        await ahead.WaitAsync(cancel.Token);
                        var job = Interlocked.Increment(ref next);
                        if (job >= jobs.Count)
                            return;
                        var result = new Decoded();
                        try
                        {
                            result.Png = await CardCache.DataAsync(jobs[job].url);
                            if (result.Png == null || result.Png.Length == 0)
                                result.Error = "empty response";
                            else
                            {
                                result.Image = PngDecoder.Decode(result.Png);
                                if (result.Image != null)
                                    result.Png = null;
                            }
                        }
                        catch (Exception e) when (!(e is OperationCanceledException))
                        {
                            result.Error = e.Message;
                            result.Image = null;
                        }
                        results[job] = result;
                    }
                }, cancel.Token);
            }

            var cursor = 0;
            var sw = new Stopwatch();
            var started = Time.realtimeSinceStartup;
            try
            {
                while (cursor < jobs.Count)
                {
                    if (generation != anim.Generation)
                        yield break;

                    sw.Restart();
                    while (cursor < jobs.Count && results.TryRemove(cursor, out var result))
                    {
                        var (track, index, url) = jobs[cursor];
                        if (result.Error != null)
                        {
                            Plugin.Log.LogError($"Card frame {url}: {result.Error}. Showing the card's still image.");
                            Unload(anim);
                            anim.Failed = true;
                            yield break;
                        }
                        track.Frames[index] = Upload(result, url, track.Layer != null ? track.Property != LayerMaps.Albedo : CardRegistry.IsLinear(track.Property));
                        track.Available = index + 1;
                        ahead.Release();
                        cursor++;
                        if (sw.ElapsedMilliseconds >= UploadBudgetMs)
                            break;
                    }
                    yield return null;
                }
            }
            finally
            {
                cancel.Cancel();
            }
        }

        public static string FrameUrl(CardAnimationTrack track, int frame) => track.Url + frame.ToString("000") + ".png";

        private static Texture2D Upload(Decoded result, string name, bool linear)
        {
            var image = result.Image;
            var texture = image != null
                ? new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, image.MipCount, linear)
                : new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
            texture.name = name;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = 4;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

            if (image == null)
            {
                if (!texture.LoadImage(result.Png, markNonReadable: true))
                    CardCache.Forget(name);
                return texture;
            }
            for (var m = 0; m < image.MipCount; m++)
                texture.SetPixelData(image.Pixels, m, image.MipOffsets[m]);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }

        private static void Unload(Anim anim)
        {
            foreach (var material in anim.Materials)
                if (anim.Stills.TryGetValue(material, out var stills))
                    foreach (var pair in stills)
                        material.SetTexture(pair.Key, pair.Value);
            if (anim.Tracks != null)
                foreach (var texture in anim.Tracks.SelectMany(t => t.Frames))
                    if (texture != null)
                        UnityEngine.Object.Destroy(texture);
            anim.Tracks = null;
            anim.Started = false;
            anim.Generation++;
            CardLayers.MarkDirty(anim.Card.Tpl);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace DaCard.Client
{
    internal static class CardCache
    {
        private const string BatchRoute = "/dacard/cards";
        private const int BatchSize = 200;
        private static readonly TimeSpan TempAge = TimeSpan.FromHours(1);

        private static string _files;
        private static string _cards;
        private static bool _ready;

        public static void Init()
        {
            try
            {
                var root = Path.Combine(BepInEx.Paths.CachePath, "DaCard");
                var legacyFonts = Path.Combine(root, "fonts");
                if (Directory.Exists(legacyFonts))
                    Directory.Delete(legacyFonts, true);
                var server = Path.Combine(root, Hex(RequestHandler.Host ?? "local").Substring(0, 12));
                _files = Path.Combine(server, "files");
                _cards = Path.Combine(server, "cards");
                Directory.CreateDirectory(_files);
                Directory.CreateDirectory(_cards);
                _ready = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("The card cache can't be used, everything is downloaded each time: " + e.Message);
            }
        }

        public static string FileOf(string url)
        {
            if (!_ready || string.IsNullOrEmpty(url))
                return null;
            var parts = url.Split('?')[0].Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var at = Array.FindIndex(parts, IsVersion);
            if (at < 2 || at == parts.Length - 1)
                return null;
            var rest = string.Join("_", parts.Skip(at + 1));
            var name = Path.GetFileNameWithoutExtension(rest) + "@" + parts[at].Substring(1) + Path.GetExtension(rest);
            return Path.Combine(_files, parts[at - 1] + "_" + Safe(name));
        }

        public static byte[] Data(string url)
        {
            var file = FileOf(url);
            var bytes = Read(file);
            if (bytes != null)
                return bytes;
            bytes = RequestHandler.GetData(url);
            Write(file, bytes);
            return bytes;
        }

        public static async Task<byte[]> DataAsync(string url)
        {
            var file = FileOf(url);
            var bytes = Read(file);
            if (bytes != null)
                return bytes;
            bytes = await RequestHandler.HttpClient.GetAsync(url);
            Write(file, bytes);
            return bytes;
        }

        public static string LocalFile(string url)
        {
            var file = FileOf(url);
            if (file != null && File.Exists(file))
                return file;
            var bytes = RequestHandler.GetData(url);
            if (bytes == null || bytes.Length == 0)
                return null;
            if (file == null)
            {
                var root = _ready ? _files : Path.Combine(BepInEx.Paths.CachePath, "DaCard");
                Directory.CreateDirectory(root);
                file = Path.Combine(root, "unversioned_" + Hex(Convert.ToBase64String(bytes)).Substring(0, 16) + Path.GetExtension(url.Split('?')[0]));
            }
            Write(file, bytes);
            return File.Exists(file) ? file : null;
        }

        public static void Forget(string url)
        {
            var file = FileOf(url);
            try
            {
                if (file != null && File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not delete the broken cached file {file}: {e.Message}");
            }
        }

        public static CardManifestEntry ReadCard(string tpl, string version)
        {
            var file = CardFile(tpl, version);
            if (file == null || !File.Exists(file))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<CardManifestEntry>(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cached card {tpl} is broken, downloading it again: {e.Message}");
                TryDelete(file);
                return null;
            }
        }

        public static void WriteCard(string tpl, string version, string json)
        {
            var file = CardFile(tpl, version);
            if (file != null && !string.IsNullOrEmpty(json) && json != "null")
                Write(file, Encoding.UTF8.GetBytes(json));
        }

        // Fetches the cards it doesn't have yet (a few at a time), then deletes what no card uses any more
        public static void Sync(ClientIndex index)
        {
            if (!_ready || index.Versions == null || index.Versions.Count == 0)
                return;
            var versions = index.Versions;
            Task.Run(async () =>
            {
                try
                {
                    var missing = versions.Where(v => !File.Exists(CardFile(v.Key, v.Value))).Select(v => v.Key).ToList();
                    for (var i = 0; i < missing.Count; i += BatchSize)
                    {
                        var chunk = missing.Skip(i).Take(BatchSize).ToList();
                        var json = await RequestHandler.PostJsonAsync(BatchRoute, JsonConvert.SerializeObject(new { tpls = chunk }));
                        var cards = string.IsNullOrEmpty(json) ? null : JObject.Load(new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None });
                        if (cards == null)
                        {
                            Plugin.Log.LogWarning("The server sent no cards for the card cache; cards are downloaded when shown");
                            return;
                        }
                        foreach (var card in cards.Properties())
                            if (versions.TryGetValue(card.Name, out var version))
                                WriteCard(card.Name, version, card.Value.ToString(Formatting.None));
                    }
                    Prune(index, versions);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Card cache update failed, cards are downloaded when shown: " + e.Message);
                }
            });
        }

        private static void Prune(ClientIndex index, Dictionary<string, string> versions)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wanted = new HashSet<string>(versions.Select(v => Path.GetFileName(CardFile(v.Key, v.Value))), StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            foreach (var file in Directory.GetFiles(_cards))
            {
                if (IsFreshTemp(file))
                    continue;
                if (!wanted.Contains(Path.GetFileName(file)))
                {
                    removed += TryDelete(file) ? 1 : 0;
                    continue;
                }
                var tpl = Path.GetFileNameWithoutExtension(file).Split('@')[0];
                var card = versions.TryGetValue(tpl, out var version) ? ReadCard(tpl, version) : null;
                foreach (var url in Urls(card))
                    Keep(keep, url);
            }

            foreach (var binder in index.Binders ?? new List<BinderManifestEntry>())
                foreach (var sticker in binder?.Stickers ?? new List<BinderSticker>())
                    Keep(keep, sticker?.Image);
            foreach (var pack in index.Packs ?? new List<PackManifestEntry>())
                foreach (var url in pack?.Textures?.Values ?? Enumerable.Empty<string>())
                    Keep(keep, url);

            foreach (var file in Directory.GetFiles(_files))
                if (!IsFreshTemp(file) && !keep.Contains(Path.GetFileName(file)))
                    removed += TryDelete(file) ? 1 : 0;
        }

        private static IEnumerable<string> Urls(CardManifestEntry card)
        {
            if (card == null)
                yield break;
            foreach (var url in (card.Textures ?? new Dictionary<string, string>()).Values)
                yield return url;
            foreach (var url in Frames(card.Animation?.Tracks?.Values))
                yield return url;
            yield return card.Text?.Name?.Font;
            yield return card.Text?.Description?.Font;
            foreach (var layer in (card.Front ?? new List<CardLayer>()).Concat(card.Back ?? new List<CardLayer>()))
            {
                if (layer == null)
                    continue;
                foreach (var url in (layer.Textures ?? new Dictionary<string, string>()).Values)
                    yield return url;
                foreach (var url in Frames(layer.Animation?.Values))
                    yield return url;
                yield return layer.Text?.Font;
            }
        }

        private static IEnumerable<string> Frames(IEnumerable<CardAnimationTrack> tracks)
        {
            foreach (var track in tracks ?? Enumerable.Empty<CardAnimationTrack>())
                if (track?.Url != null)
                    for (var i = 0; i < track.Frames; i++)
                        yield return CardAnimator.FrameUrl(track, i);
        }

        private static void Keep(HashSet<string> keep, string url)
        {
            var file = FileOf(url);
            if (file != null)
                keep.Add(Path.GetFileName(file));
        }

        private static string CardFile(string tpl, string version) =>
            _ready && !string.IsNullOrEmpty(tpl) && !string.IsNullOrEmpty(version) ? Path.Combine(_cards, Safe(tpl + "@" + version + ".json")) : null;

        private static byte[] Read(string file)
        {
            if (file == null || !File.Exists(file))
                return null;
            try
            {
                var bytes = File.ReadAllBytes(file);
                return bytes.Length > 0 ? bytes : null;
            }
            catch
            {
                return null;
            }
        }

        private static void Write(string file, byte[] bytes)
        {
            if (file == null || bytes == null || bytes.Length == 0 || File.Exists(file))
                return;
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (!File.Exists(file))
                    File.Move(temp, file);
            }
            catch (Exception e)
            {
                if (!File.Exists(file))
                    Plugin.Log.LogWarning($"Could not save {Path.GetFileName(file)} in the card cache: {e.Message}");
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static bool IsFreshTemp(string file) =>
            file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TempAge;

        private static bool TryDelete(string file)
        {
            try
            {
                if (!File.Exists(file))
                    return false;
                File.Delete(file);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsVersion(string part) => part.Length > 1 && part[0] == 'v' && part.Skip(1).All(char.IsDigit);

        private static string Safe(string name) => new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        private static string Hex(string text)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }
}

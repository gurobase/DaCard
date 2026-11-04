using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DaCard.Server.Storage;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Modding.Custom;
using Path = System.IO.Path;

namespace DaCard.Server;

[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class DaCardMod(
    ISptLogger<DaCardMod> logger,
    ModHelper modHelper,
    DashboardUpgrade upgrade,
    DaCardDatabase database,
    CardStore store,
    CardIndex index,
    CollectionBinders binders,
    CustomItemService customItemService,
    CardLoot cardLoot,
    TradersTable tradersTable,
    GeekTrader geek,
    BoosterPacks boosterPacks,
    ItemLedger ledger,
    DaCardHandbook handbook,
    CardTrading trading,
    RagfairConfig ragfairConfig,
    ItemConfig itemConfig,
    ItemFilterService itemFilterService) : IOnLoad
{
    public static string DefaultBundle(string type) => $"dacard/item_card_{type}.bundle";

    internal const string CloneTpl = "5f745ee30acaeb0d490d8c5b";
    internal const string ParentJewelry = "57864a3d24597754843f8721";
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    public const string DefaultConfigFile = "defaults/config.json";

    private static readonly List<TextureSlot> DefaultSlots =
    [
        new() { Suffix = "", Property = "_MainTex", Required = true },
        new() { Suffix = "height", Property = "_HeightMap", Linear = true, Default = "white" },
        new() { Suffix = "holo", Property = "_HoloMask", Linear = true, Default = "white" },
        new() { Suffix = "foil", Property = "_FoilMask", Linear = true, Default = "white" },
        new() { Suffix = "normal", Property = "_NormalMap", Linear = true, Default = "bump" },
        new() { Suffix = "roughness", Property = "_PictureRoughnessMap", Linear = true, Default = "white", Flag = "_HasPictureRoughness" },
        new() { Suffix = "metallic", Property = "_PictureMetallicMap", Linear = true, Default = "black", Flag = "_HasPictureMetallic" }
    ];

    private static readonly Dictionary<string, CardTypeSettings> DefaultCardTypes = new()
    {
        ["3d"] = new CardTypeSettings(),
        ["2d"] = new CardTypeSettings { Slots = ["", "holo", "foil", "normal"] }
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        index.Clear();
        handbook.Register();
        upgrade.Run(modPath);
        var problem = database.Open(modPath);
        if (problem != null)
        {
            logger.Error($"[DaCard] No cards, binders or booster packs: {problem}.");
            return Task.CompletedTask;
        }
        ledger.Load();
        var config = store.LoadConfig(Path.Combine(modPath, DefaultConfigFile));
        index.Config = config;

        var slots = config.Textures.Count > 0 ? config.Textures : DefaultSlots;
        if (slots.All(s => s.Suffix != ""))
            slots.Insert(0, new TextureSlot { Suffix = "", Property = "_MainTex", Required = true });
        index.Slots = slots;
        var configuredTypes = config.CardTypes.Count > 0 ? config.CardTypes : DefaultCardTypes;
        var cardTypes = AvailableTypes(modPath, configuredTypes);
        index.Types = new Dictionary<string, CardTypeSettings>(cardTypes, StringComparer.OrdinalIgnoreCase);
        index.LegacyTemplates = store.LegacyTemplates();

        var collections = store.Collections();
        var collectionById = collections.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var layers = store.AllLayers();
        var layersOf = layers.GroupBy(l => (l.OwnerKind, l.OwnerId)).ToDictionary(g => g.Key, g => g.ToList());

        var hidden = store.HiddenLayers();
        var epoch = Hash(JsonSerializer.Serialize(config, DaCardDatabase.Json) + "|" + ModMetadata.CurrentVersion + "|" + string.Join(",", cardTypes.Keys));
        var shownAs = new Dictionary<string, int>();
        var misplaced = new Dictionary<string, int>();
        var created = new List<CardRow>();
        foreach (var collection in collections)
        {
            index.Rarities[collection.Id] = CardRarities.Of(collection, config);
            index.FoilTypes[collection.Id] = CardRarities.FoilTypesOf(collection);
        }
        foreach (var card in store.Cards())
        {
            if (!collectionById.TryGetValue(card.CollectionId, out var collection))
                continue;
            index.Stored.Add(card.Id);
            var rarities = index.Rarities[collection.Id];
            var rarity = CardRarities.Find(rarities, card.Rarity);
            if (rarity == null)
            {
                rarity = rarities[0];
                misplaced[$"{collection.Name}: {card.Rarity}"] = misplaced.GetValueOrDefault($"{collection.Name}: {card.Rarity}") + 1;
            }
            var (typeName, type) = ResolveType(card, cardTypes, configuredTypes, shownAs);
            var bundle = type.Bundle ?? DefaultBundle(typeName);
            var usesDepth = type.Slots == null || type.Slots.Contains("height", StringComparer.OrdinalIgnoreCase);
            var declared = card.Type.Trim().ToLowerInvariant();

            var template = TemplateFor(collection, rarity.Name, typeName, rarity.Settings, bundle);
            if (template == null)
                continue;

            var foilChance = Math.Clamp(collection.FoilChance ?? config.Foil.Percent, 0, 100);
            var stack = RollLayers(card, layersOf.GetValueOrDefault(("card", card.Id)) ?? [],
                layersOf.GetValueOrDefault(("collection", card.CollectionId)) ?? [], hidden, foilChance);
            if (usesDepth || declared != typeName)
                stack.Insert(0, new RollLayer(CardManifests.PictureLayerId, 100, 0, 0, true, foilChance, null));
            foreach (var layer in stack.Where(l => l.Group == null))
                ledger.RecordLayer(card.Id, layer.Id, layer.Chance);
            index.Layers[card.Id] = stack.ToArray();

            created.Add(card);
            index.Cards[card.Id] = new CreatedCard(card.Id, card.CollectionId, rarity.Name, rarity.Rank, declared, typeName, usesDepth, template.Id);
            index.Versions[card.Id] = Hash($"{card.UpdatedAt}|{collection.UpdatedAt}|{typeName}|{usesDepth}|{template.Id}|{epoch}");
        }
        index.Seal();

        foreach (var (what, count) in shownAs)
            logger.Info($"[DaCard] {count} {what}: that card type's bundle is missing, so they are shown flat.");
        foreach (var (what, count) in misplaced)
            logger.Warning($"[DaCard] {count} card(s) of {what}: the collection has no such rarity, so they count as its first rarity. Pick their rarity in the dashboard.");

        if (collections.Count > 0 && !File.Exists(Path.Combine(modPath, "bundles", CollectionBinders.BundlePath)))
            logger.Error($"[DaCard] bundles/{CollectionBinders.BundlePath} is missing, binders will have no model. Build the bundles in Unity.");
        var binderEntries = binders.CreateBinders(collections, created, store.BinderStickers(), config.Binders);

        var packs = boosterPacks.Create(store.Packs(), config);
        if (packs.Count > 0 && !File.Exists(Path.Combine(modPath, "bundles", BoosterPacks.BundlePath)))
            logger.Error($"[DaCard] bundles/{BoosterPacks.BundlePath} is missing, booster packs will have no model. Build the bundles in Unity.");

        ledger.RetireMissing(modPath, config.RetiredItems, store.Exists, index.LegacyTemplates.ContainsKey);

        index.Client = new ClientIndex
        {
            Slots = slots,
            BackProperty = string.IsNullOrWhiteSpace(config.BackProperty) ? "_CARD_BACK" : config.BackProperty,
            OverlayProperty = string.IsNullOrWhiteSpace(config.OverlayProperty) ? "_CARD_FRONT_BORDER" : config.OverlayProperty,
            Versions = new Dictionary<string, string>(index.Versions),
            Templates = index.Templates.Values.ToDictionary(t => t.Id, t => new ClientTemplate { Collection = t.CollectionId, Rarity = t.Rarity, Price = t.Price }),
            Binders = binderEntries,
            Packs = packs,
            Fonts = layers.Any(l => !string.IsNullOrWhiteSpace(l.Text?.Font)) || collections.Any(c => c.CardText?.Name?.Font != null || c.CardText?.Description?.Font != null)
        };

        Blacklist(index.Templates.Keys.Concat(ledger.Placeholders).Select(t => new MongoId(t)).ToList());
        trading.Enable();
        cardLoot.Configure(config);
        if (geek.Add(modPath))
        {
            AddTraderOffers(binderEntries.ToDictionary(b => b.Tpl, _ => config.Binders.Price), "binder");
            AddTraderOffers(boosterPacks.Offers, "booster pack");
            if (config.Geek.SellCards)
                AddTraderOffers(index.Templates.Values.Where(t => index.CardsOfTemplate.ContainsKey(t.Id)).ToDictionary(t => t.Id, t => t.Price), "random card");
            SetBuyers(index.Templates.Keys.Concat(binderEntries.Select(b => b.Tpl)).Concat(packs.Select(p => p.Tpl)).Concat(ledger.Placeholders).ToList());
        }

        var perRarity = string.Join(", ", index.Cards.Values.GroupBy(c => c.Rarity, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Min(c => c.RarityRank))
            .Select(g => $"{g.Key} {g.Count()}"));
        var perType = string.Join(", ", index.Cards.Values.GroupBy(c => c.TypeName).Select(g => $"{g.Count()} {g.Key.ToUpperInvariant()}"));
        logger.Success($"[DaCard] {ModMetadata.CurrentVersion} loaded {index.Cards.Count} card(s) as {index.Templates.Count} item(s): " +
                       $"{perRarity} ({perType}); {binderEntries.Count} collection binder(s); {packs.Count} booster pack(s); {collections.Count} collection(s)");
        return Task.CompletedTask;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    public static string TemplateId(CollectionRow collection, string rarity, string typeName) =>
        CardCatalog.IdFor($"cardtpl:{collection.IdKey}:{rarity}:{typeName}");

    private CardTemplate? TemplateFor(CollectionRow collection, string rarityName, string typeName, RaritySettings rarity, string bundle)
    {
        var tpl = TemplateId(collection, rarityName, typeName);
        if (index.Templates.TryGetValue(tpl, out var existing))
            return existing;
        var price = CardPrice(rarity);
        if (!CreateTemplate(tpl, collection, rarityName, typeName, rarity, bundle, price))
            return null;
        var template = new CardTemplate(tpl, collection.Id, rarityName, typeName, price);
        index.Templates[tpl] = template;
        return template;
    }

    private static List<RollLayer> RollLayers(CardRow card, List<LayerRow> own, List<LayerRow> collection,
        Dictionary<string, (bool CollectionLayers, List<string> Hidden)> hiddenLayers, double foilChance)
    {
        var found = hiddenLayers.TryGetValue(card.Id, out var h);
        var hidden = (found ? h.Hidden : []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hideAll = found && !h.CollectionLayers;
        RollLayer Make(LayerRow l, double chance) => new(l.Id, chance, Math.Max(0, l.Price), l.PricePercent, l.CanBeFoil,
            Math.Clamp(l.FoilChance ?? foilChance, 0, 100), CardRarities.FoilTypes.Contains(l.FoilType) ? l.FoilType : null);
        IEnumerable<RollLayer> Expand(LayerRow l, double chance, List<LayerRow> rows)
        {
            if (!l.IsVariantLayer)
            {
                yield return Make(l, chance);
                yield break;
            }
            yield return new RollLayer(l.Id, chance, 0, 0, false, 0, null, null, true);
            foreach (var variant in rows.Where(v => v.ParentId == l.Id).OrderBy(v => v.Position))
                yield return Make(variant, Math.Max(0, variant.Chance)) with { Group = l.Id };
        }
        IEnumerable<RollLayer> Own(LayerRow l) => Expand(l, Math.Clamp(l.Chance, 0, 100), own);
        IEnumerable<RollLayer> Coll(LayerRow l) => Expand(l, hideAll || hidden.Contains(l.Id) ? 0 : Math.Clamp(l.Chance, 0, 100), collection);
        var stack = new List<RollLayer>();
        foreach (var face in new[] { "front", "back" })
        {
            var mine = own.Where(l => l.Face == face && l.ParentId == null).OrderBy(l => l.Position).ToList();
            var theirs = collection.Where(l => l.Face == face && l.ParentId == null).OrderBy(l => l.Position).ToList();
            var part = mine.Where(l => !l.Over).SelectMany(Own).Concat(theirs.SelectMany(Coll)).Concat(mine.Where(l => l.Over).SelectMany(Own)).ToList();
            if (face == "back" && part.Count == 0)
                part = collection.Where(l => l.Face == "default-back" && l.ParentId == null).SelectMany(Coll).ToList();
            stack.AddRange(part);
        }
        return stack;
    }

    private Dictionary<string, CardTypeSettings> AvailableTypes(string modPath, Dictionary<string, CardTypeSettings> types)
    {
        var available = types.Where(t => File.Exists(Path.Combine(modPath, "bundles", t.Value.Bundle ?? DefaultBundle(t.Key.ToLowerInvariant()))))
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.OrdinalIgnoreCase);
        if (available.Count > 0)
            return available;

        foreach (var (name, type) in types)
            logger.Error($"[DaCard] Card type \"{name}\": bundles/{type.Bundle ?? DefaultBundle(name.ToLowerInvariant())} is missing, its cards will have no model. " +
                         "Build the mod in Unity (DaCard > Build Mod) with a material for this type.");
        return new Dictionary<string, CardTypeSettings>(types, StringComparer.OrdinalIgnoreCase);
    }

    private (string Name, CardTypeSettings Settings) ResolveType(CardRow card, Dictionary<string, CardTypeSettings> types,
        Dictionary<string, CardTypeSettings> configured, Dictionary<string, int> shownAs)
    {
        var name = string.IsNullOrWhiteSpace(card.Type) ? "2d" : card.Type.Trim().ToLowerInvariant();
        if (types.TryGetValue(name, out var match))
            return (name, match);

        var fallback = types.ContainsKey("2d") ? "2d" : types.Keys.First().ToLowerInvariant();
        if (configured.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            var what = $"{name.ToUpperInvariant()} card(s)";
            shownAs[what] = shownAs.GetValueOrDefault(what) + 1;
        }
        else
        {
            logger.Error($"[DaCard] {card.Name} ({card.Rarity}): unknown type \"{card.Type}\" (settings card types: {string.Join(", ", configured.Keys)}); using {fallback}.");
        }
        return (fallback, types[fallback]);
    }

    public static double CardPrice(RaritySettings rarity) => rarity.Price > 0 ? rarity.Price : 1000;

    private bool CreateTemplate(string tpl, CollectionRow collection, string rarityName, string typeName, RaritySettings rarity, string bundle, double price)
    {
        var tint = !rarityName.Equals("Common", StringComparison.OrdinalIgnoreCase)
                   && Regex.IsMatch(rarity.Color, "^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")
            ? "#" + rarity.Color.TrimStart('#')
            : null;
        string Colored(string name) => tint != null ? $"<color={tint}>{name}</color>" : name;

        LocaleDetails Locale(string name, string shortName) => new()
        {
            Name = Colored($"{name} card ({rarityName})"),
            ShortName = Colored(shortName),
            Description = $"A {rarityName} card from the {name} collection."
        };
        var locales = new Dictionary<string, LocaleDetails> { ["en"] = Locale(collection.Name, collection.ShortName ?? collection.Name) };
        foreach (var (lang, text) in collection.Locales ?? new())
            locales[lang] = Locale(text.Name ?? collection.Name, text.ShortName ?? text.Name ?? collection.ShortName ?? collection.Name);

        var result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = CollectionBinders.CloneTpl,
            ParentId = CollectionBinders.ParentCompoundItem,
            NewId = new MongoId(tpl),
            NewItemName = $"dacard_cards_{Regex.Replace(collection.IdKey.ToLowerInvariant(), "[^a-z0-9_]", "_")}_{rarityName.ToLowerInvariant()}_{typeName}",
            FleaPriceRoubles = price,
            HandbookPriceRoubles = price,
            HandbookParentId = DaCardHandbook.Cards,
            AddToHandbook = true,
            AddToFleaPriceDb = true,
            Locales = locales,
            OverrideProperties = new TemplateItemProperties
            {
                Prefab = new Prefab { Path = bundle, Rcid = "" },
                Width = 1,
                Height = 1,
                Weight = 0.005,
                BackgroundColor = rarity.Background,
                ExaminedByDefault = true,
                CanSellOnRagfair = false,
                ItemSound = "jewelry",
                LootExperience = 100,
                ExamineExperience = 100,
                MergesWithChildren = false,
                HideEntrails = false,
                Grids = [],
                Slots = []
            }
        }, Assembly.GetExecutingAssembly());

        if (!result.Success)
        {
            logger.Error($"[DaCard] Could not create the {rarityName} {typeName.ToUpperInvariant()} cards of '{collection.Name}': {string.Join("; ", result.Errors ?? [])}");
            return false;
        }

        ragfairConfig.Dynamic.Blacklist.Custom.Add(new MongoId(tpl));
        ledger.Record(tpl, new LedgerItem
        {
            Kind = ItemLedger.CardTemplate,
            Key = $"{collection.IdKey}:{rarityName}:{typeName}",
            Name = $"{collection.Name} card ({rarityName})",
            Price = price,
            Background = rarity.Background,
            Bundle = bundle,
            Owner = collection.Id
        });
        return true;
    }

    private void Blacklist(List<MongoId> tpls)
    {
        if (tpls.Count == 0)
            return;
        itemFilterService.AddItemToBlacklistCache(tpls);
        itemConfig.Blacklist.UnionWith(tpls);
        itemConfig.RewardItemBlacklist.UnionWith(tpls);
    }

    private void AddTraderOffers(Dictionary<string, double> prices, string what)
    {
        if (prices.Count == 0 || !tradersTable.TryGetValue(GeekTrader.Id, out var trader))
            return;

        var assort = trader.Assort;
        foreach (var (tpl, price) in prices)
        {
            var offerId = new MongoId(CardCatalog.IdFor("trader-offer:" + tpl));
            assort.Items.Add(new Item
            {
                Id = offerId,
                Template = new MongoId(tpl),
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = true, StackObjectsCount = 999999 }
            });
            assort.BarterScheme[offerId] = [[new BarterScheme { Count = price, Template = new MongoId(Roubles) }]];
            assort.LoyalLevelItems[offerId] = 1;
        }

        logger.Info($"[DaCard] {trader.Base.Nickname} sells {prices.Count} {what}(s)");
    }

    private void SetBuyers(List<string> tpls)
    {
        if (tpls.Count == 0)
            return;

        var ids = tpls.Select(t => new MongoId(t)).ToList();
        var wanted = new HashSet<MongoId> { GeekTrader.Id };

        var names = new List<string>();
        foreach (var (id, trader) in tradersTable)
        {
            var buys = wanted.Contains(id);
            var list = buys
                ? trader.Base.ItemsBuy ??= new ItemBuyData { Category = [], IdList = [] }
                : trader.Base.ItemsBuyProhibited ??= new ItemBuyData { Category = [], IdList = [] };
            list.IdList.UnionWith(ids);
            if (buys)
                names.Add(trader.Base.Nickname ?? id.ToString());
        }
        logger.Info($"[DaCard] Cards, binders and booster packs are bought by: {string.Join(", ", names)}");
    }
}

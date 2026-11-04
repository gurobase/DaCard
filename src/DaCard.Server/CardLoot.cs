using DaCard.Server.Storage;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils.Collections;
using System.Reflection;

namespace DaCard.Server;

[Injectable(InjectionType.Singleton)]
public class CardLoot(ISptLogger<CardLoot> logger, CardIndex cardIndex)
{
    private static CardLoot? _instance;
    private static bool _patched;

    private HashSet<MongoId> _containers = new();
    private RarityRoller? _cards;
    private double _cardPercent;
    private readonly CardIndex _index = cardIndex;

    // Special SPT loot entry representing "one random MTG card".
    // This is NOT an actual EFT item template.
    private static readonly MongoId MtzCardPoolTpl = new MongoId();

    public void Configure(DaCardConfig config)
    {
        _containers = config.Containers
            .Select(c => new MongoId(c))
            .ToHashSet();

        _cardPercent = Math.Clamp(config.Loot.CardPercent, 0, 100);

        _cards = RarityRoller.Build(
            _index.Cards.Values.Select(c =>
                (c.Id, c.CollectionId, c.Rarity)),
            _index.RaritiesOf);

        _instance = this;

        if (_patched)
            return;

        new CreateStaticLootItemPatch().Enable();
        new PossibleLootItemsPatch().Enable();
        _patched = true;
    }

    private void LogError(string message)
    {
        logger.Error(message);
    }

    private void LogInfo(string message)
    {
        logger.Info(message);
    }

    private class CardLootState
    {
        public string? CardId;
    }

    private class CreateStaticLootItemPatch : AbstractPatch
    {
        protected override MethodBase GetTargetMethod() => typeof(LocationLootGenerator).GetMethod(
            "CreateStaticLootItem",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(MongoId), typeof(Dictionary<string, IEnumerable<StaticAmmoDetails>>), typeof(string)],
            null
        )!;

        [PatchPrefix]
        public static void Prefix(ref MongoId chosenTpl, out CardLootState __state)
        {
            __state = new CardLootState();
            if (_instance == null || _instance._cards == null || chosenTpl != MtzCardPoolTpl)
                return;
            
            _instance.LogInfo($"Number of cards in prefix: {_instance._cards.Count}");

            string? cardId = _instance._cards.Pick(Random.Shared);

            if (cardId == null)
            {
                _instance.LogError("[DaCard] RarityRoller returned no card.");
                return;
            }

            CreatedCard? card = _instance._index.Find(cardId);

            if (card == null)
            {
                _instance.LogError($"[DaCard] Card '{cardId}' was not found in CardIndex.");
                return;
            }

            __state.CardId = cardId;
            chosenTpl = new MongoId(card.Template);
        }

        [PatchPostfix]
        public static void PostFix(CardLootState __state, ref ContainerItem? __result)
        {
            if (_instance == null || string.IsNullOrEmpty(__state.CardId) || __result?.Items == null) return;

            Item? root = __result.Items.FirstOrDefault();
            if (root == null) return;

            root.Upd ??= new Upd();
            root.Upd.ExtensionData ??= [];
            root.Upd.ExtensionData["DaCard"] = new Dictionary<string, object>
            {
                ["c"] = __state.CardId,
                ["1"] = Array.Empty<string>()
            };
        }
    }

    private class PossibleLootItemsPatch : AbstractPatch
    {
        protected override MethodBase GetTargetMethod() => typeof(LocationLootGenerator).GetMethod(
            "GetPossibleLootItemsForContainer",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(MongoId), typeof(Dictionary<MongoId, StaticLootDetails>)],
            null
        )!;

        [PatchPostfix]
        public static void Postfix(MongoId containerTypeId, Dictionary<MongoId, StaticLootDetails> staticLootDist, ref ProbabilityObjectArray<MongoId, float?> __result)
        {
            try
            {
                AddCardPool(containerTypeId, staticLootDist, ref __result);
            }
            catch (Exception e)
            {
                _instance?.LogError($"[DaCard] Failed to add MTG pool to container {containerTypeId}: {e}");
            }
        }

        private static void AddCardPool(MongoId containerTypeId, Dictionary<MongoId, StaticLootDetails> staticLootDist, ref ProbabilityObjectArray<MongoId, float?> result)
        {
            if (_instance == null || !_instance._containers.Contains(containerTypeId))
                return;

            if (_instance._cardPercent <= 0)
                return;

            if (!staticLootDist.TryGetValue(containerTypeId, out StaticLootDetails? lootDetails))
                return;

            if (lootDetails?.ItemDistribution == null)
                return;

            double existingWeight = lootDetails.ItemDistribution
                .Where(x => x.RelativeProbability.HasValue)
                .Sum(x => (double)x.RelativeProbability!.Value);

            if (existingWeight <= 0)
                return;

            double probability = _instance._cardPercent / 100.0;

            if (probability >= 1.0)
                return;
            double poolWeight = existingWeight * probability / (1.0 - probability);
            result.Add(new ProbabilityObject<MongoId, float?>(MtzCardPoolTpl, (float)poolWeight, null));
        }
    }
}
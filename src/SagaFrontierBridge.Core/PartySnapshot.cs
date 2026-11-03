using System;

namespace SagaFrontierBridge.Core
{
    public sealed class PartySnapshot
    {
        public bool Available { get; init; }

        public string? Error { get; init; }

        public string? Source { get; init; }

        public bool? SaveLoaded { get; init; }

        public SaveWorkParty? Party { get; init; }

        public int? RosterSlotCount { get; init; }

        public int[]? ActiveCharacterIds { get; init; }

        public int[]? RosterCharacterIds { get; init; }

        public PartyMemberSnapshot[]? Members { get; init; }

        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    public sealed class PartyMemberSnapshot
    {
        public int CharacterId { get; init; }

        public int? HpMax { get; init; }

        public int? LpNow { get; init; }

        public int? LpMax { get; init; }

        public int? WpNow { get; init; }

        public int? WpMax { get; init; }

        public int? JpNow { get; init; }

        public int? JpMax { get; init; }

        public int? StrNow { get; init; }

        public int? DexNow { get; init; }

        public int? HndNow { get; init; }

        public int? MndNow { get; init; }

        public int? MgcNow { get; init; }

        public int? ConNow { get; init; }

        public int? ChmNow { get; init; }

        public int? Race { get; init; }

        public int? Nature { get; init; }

        public int? CharacterType { get; init; }

        public int? AllLpLoss { get; init; }

        public int? BattleCount { get; init; }

        public int[]? EquipmentItemIds { get; init; }

        public int[]? EquipmentAttackIds { get; init; }
    }
}

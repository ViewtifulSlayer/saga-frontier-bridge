using System;

namespace SagaFrontierBridge.Core
{
    public sealed class SaveWorkSnapshot
    {
        public bool Available { get; init; }

        public string? Error { get; init; }

        public string? Source { get; init; }

        public bool? SaveLoaded { get; init; }

        public int? Test { get; init; }

        public bool? IsTakeover { get; init; }

        public SaveWorkParty? Party { get; init; }

        public int[]? PartyCharacterIds { get; init; }

        public int? RosterCount { get; init; }

        public int? SumLength { get; init; }

        public string? SumSha256 { get; init; }

        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    public sealed class SaveWorkParty
    {
        public int? PsnCnt { get; init; }

        public int? HeroNo { get; init; }

        public string? Name { get; init; }

        public string? Name2 { get; init; }

        public uint? Money { get; init; }

        public int? ParLev { get; init; }

        public int? PlayTimeHours { get; init; }

        public int? PlayTimeMinutes { get; init; }

        public double? PlayTimeSeconds { get; init; }
    }
}

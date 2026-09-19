using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Rooms;

namespace MirrorDuelistMod;

/// <summary>
/// Single-monster normal encounter, modelled on FrogKnightNormal. Gold rewards
/// are left at the EncounterModel defaults, matching ThievingHopper.
/// </summary>
public sealed class MirrorDuelistNormal : EncounterModel
{
    public override RoomType RoomType => RoomType.Monster;

    public override IEnumerable<MonsterModel> AllPossibleMonsters =>
        new[] { ModelDb.Monster<MirrorDuelist>() };

    protected override IReadOnlyList<(MonsterModel, string?)> GenerateMonsters()
    {
        return new[] { (ModelDb.Monster<MirrorDuelist>().ToMutable(), (string?)null) };
    }
}

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;

namespace MirrorDuelistMod;

[ModInitializer(initializerMethod: nameof(Initialize))]
public static class ModInit
{
    private static void Initialize()
    {
        new Harmony("fastbit-ljb.MirrorDuelist").PatchAll(typeof(ModInit).Assembly);
    }
}

/// <summary>
/// Workshop hot-reloaders may load the DLL in a collectible context which the
/// game's normal model scan cannot see. Inject is idempotent, so this fallback
/// is also safe during a normal launch.
/// </summary>
[HarmonyPatch(typeof(ModelDb), nameof(ModelDb.Init))]
internal static class ModelDbRegistrationPatch
{
    [HarmonyPostfix]
    private static void RegisterModels()
    {
        ModelDb.Inject(typeof(MirrorDuelist));
        ModelDb.Inject(typeof(MirrorDuelistNormal));
        ModelDb.Inject(typeof(MirrorOstyGuardPower));
    }
}

[HarmonyPatch]
internal static class MirrorDuelistEncounterPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Glory), nameof(Glory.GenerateAllEncounters))]
    private static void AddToGloryPool(ref IEnumerable<EncounterModel> __result)
    {
        __result = __result.Append(ModelDb.Encounter<MirrorDuelistNormal>());
    }

    /// <summary>
    /// Put exactly one Mirror Duelist in a random one of Act 3's first three
    /// normal encounters. The generated room list is saved with the run, so no
    /// separate save-state flag is required.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ActModel), nameof(ActModel.GenerateRooms))]
    private static void PlaceInFirstThreeFights(ActModel __instance, Rng rng)
    {
        if (__instance.Index != 2)
        {
            return;
        }
        if (AccessTools.Field(typeof(ActModel), "_rooms")?.GetValue(__instance) is not RoomSet rooms)
        {
            return;
        }
        List<EncounterModel> encounters = rooms.normalEncounters;
        encounters.RemoveAll(e => e is MirrorDuelistNormal);
        int candidateCount = System.Math.Min(3, encounters.Count);
        if (candidateCount == 0)
        {
            return;
        }
        encounters[rng.NextInt(candidateCount)] = ModelDb.Encounter<MirrorDuelistNormal>();
    }
}

/// <summary>
/// Vanilla AOE powers (Noxious Fumes, The Bomb, ...) target
/// CombatState.HittableEnemies, which is hardcoded to the monster side. When
/// the OWNER of such a power is the duelist (enemy side), the effect would hit
/// the duelist's own team. During the enemy turn in a duelist fight, flip the
/// getter to the player side. Player-turn calls are untouched, and so is
/// every combat without a Mirror Duelist on the field.
/// </summary>
[HarmonyPatch(typeof(CombatState), "get_HittableEnemies")]
internal static class HittableEnemiesRedirectPatch
{
    [HarmonyPostfix]
    private static void RedirectToPlayersDuringDuelistTurn(CombatState __instance, ref IReadOnlyList<Creature> __result)
    {
        if (__instance.CurrentSide != CombatSide.Enemy)
        {
            return;
        }
        bool hasDuelist = false;
        foreach (Creature enemy in __instance.Enemies)
        {
            if (enemy.Monster is MirrorDuelist)
            {
                hasDuelist = true;
                break;
            }
        }
        if (!hasDuelist)
        {
            return;
        }
        __result = __instance.Allies.Where(c => c != null && c.IsAlive && c.IsHittable).ToList();
    }
}

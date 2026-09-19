using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

namespace MirrorDuelistMod;

/// <summary>
/// Creates a lightweight player object whose combat creature is the duelist.
/// It is used only as the owner of copied cards and their private combat piles;
/// stolen cards are resolved by MirrorDuelist's UI-free interpreter rather
/// than by invoking arbitrary vanilla CardModel.OnPlay methods.
/// </summary>
internal static class MirrorPlayerFactory
{
    public static Player? Create(Player real, Creature duelist)
    {
        try
        {
            var ctor = typeof(Player).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .First();
            int paramCount = ctor.GetParameters().Length;
            var args = new List<object?>
            {
                real.Character, real.NetId + 777uL, 999, 999, 5, 0, 3, 0,
                new MegaCrit.Sts2.Core.Runs.RelicGrabBag(), real.UnlockState,
            };
            while (args.Count < paramCount)
            {
                args.Add(null);
            }
            var fake = (Player)ctor.Invoke(args.ToArray());
            var creatureField = typeof(Player).GetField(
                "<Creature>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            creatureField?.SetValue(fake, duelist);
            fake.RunState = real.RunState;
            return fake;
        }
        catch (System.Exception e)
        {
            Log.Error("[MirrorDuelist] mirror player creation failed: " + e);
            return null;
        }
    }
}

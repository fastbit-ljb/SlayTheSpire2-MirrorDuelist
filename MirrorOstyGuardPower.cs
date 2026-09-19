using System.Linq;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.ValueProps;

namespace MirrorDuelistMod;

/// <summary>
/// Hidden equivalent of Osty's Die For You power. Mirror Osty is deliberately
/// a normal enemy (not a Player pet) so the combat room keeps it on the enemy
/// side and does not scramble the player/enemy visual containers.
/// </summary>
public sealed class MirrorOstyGuardPower : PowerModel
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Single;

    protected override bool IsVisibleInternal => false;

    public override bool ShouldPlayVfx => false;

    public override Creature ModifyUnblockedDamageTarget(
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer)
    {
        if (target != Owner || !props.IsPoweredAttack())
        {
            return target;
        }
        Creature? osty = Owner.CombatState?.Enemies
            .FirstOrDefault(c => c.IsAlive && c.Monster is Osty);
        return osty ?? target;
    }
}

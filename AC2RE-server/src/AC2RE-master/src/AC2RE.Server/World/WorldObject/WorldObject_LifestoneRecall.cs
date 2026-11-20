using AC2RE.Definitions;
using System.Numerics;

namespace AC2RE.Server;

internal partial class WorldObject {

    private sealed record LifestoneCast(PositionOffset origin, PositionOffset destination,
        double completesAt, BehaviorId behavior, SkillInfo skill, uint attackId);

    private LifestoneCast? pendingLifestoneRecall;

    private void startLifestoneRecall(ActiveSkill skill, SkillInfo info, uint attackId) {
        if (player == null || dead || !inWorld || !info.flags.HasFlag(SkillInfo.Flag.IsTrained)) {
            finishSelfCast(SkillId.Com_LifestoneRecall, attackId, ErrorType.RecallFailed);
            return;
        }
        if (lifestoneBind is not PositionOffset destination) {
            failLifestoneRecall(attackId, "Use a nearby lifestone to bind before casting Lifestone Recall.");
            return;
        }
        double now = world.serverTime.time;
        if (pendingLifestoneRecall != null) {
            failLifestoneRecall(attackId, "Lifestone Recall is already casting.");
            return;
        }
        if (info.flags.HasFlag(SkillInfo.Flag.HasTimeLastUsed) && now - info.lastUsedTime < skill.resetTime) {
            failLifestoneRecall(attackId, "Lifestone Recall is still recharging.");
            return;
        }
        if (vigor < skill.minVigor) {
            finishSelfCast(SkillId.Com_LifestoneRecall, attackId, ErrorType.Combat_NotEnoughVigor);
            return;
        }

        setAttacking(false);
        BehaviorId behavior = skill.behaviorSelfHit;
        pendingLifestoneRecall = new(offset, destination, now + skill.powerupTime, behavior, info, attackId);
        if (behavior != BehaviorId.Undef) {
            doBehavior(new() { behaviorId = behavior, timeScale = 1 });
        }
    }

    private void advanceLifestoneRecall(double now) {
        if (pendingLifestoneRecall is not LifestoneCast cast) {
            return;
        }
        if (!inWorld || player == null || dead) {
            cancelLifestoneRecall();
            return;
        }
        if (offset.cell != cast.origin.cell || Vector3.DistanceSquared(offset.offset, cast.origin.offset) > 0.25f) {
            cancelLifestoneRecall("Moving interrupted your Lifestone Recall.");
            return;
        }
        if (now < cast.completesAt) {
            return;
        }

        pendingLifestoneRecall = null;
        if (cast.behavior != BehaviorId.Undef) {
            stopBehavior(cast.behavior);
        }
        world.playerManager.send(player, new EnterPortalSpaceCEvt());
        teleport(player, cast.destination);
        world.playerManager.send(player, new ExitPortalSpaceCEvt());
        completeSelfCast(SkillId.Com_LifestoneRecall, cast.skill, cast.attackId, now);
    }

    private void cancelLifestoneRecall(string? reason = null) {
        if (pendingLifestoneRecall is not LifestoneCast cast) {
            return;
        }
        pendingLifestoneRecall = null;
        if (inWorld && cast.behavior != BehaviorId.Undef) {
            stopBehavior(cast.behavior);
        }
        if (reason != null) {
            failLifestoneRecall(cast.attackId, reason);
        }
    }

    private void failLifestoneRecall(uint attackId, string message) {
        if (player != null) {
            world.playerManager.send(player, new DisplayStringInfoMsg {
                text = new StringInfo(message + "\n"),
                type = TextType.Standard,
            });
        }
        finishSelfCast(SkillId.Com_LifestoneRecall, attackId, ErrorType.None);
    }
}


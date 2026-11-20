using AC2RE.Definitions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AC2RE.Server;

internal partial class WorldObject {

    // QualitiesEffect encodes the stat type in the high word (3 = float).
    private const uint COMBAT_ANIMATION_SCALE = (3u << 16) | (uint)FloatStat.Physics_CombatAnimScale;
    private const uint SKILL_RATING = 0x00800000;

    private bool isSelfCast(ActiveSkill skill, InstanceId targetId) {
        return skill.validTargets.HasFlag(ActiveSkill.Target.Self)
            && !skill.activeFlags.HasFlag(ActiveSkill.Flag.IsHarmful)
            && (skill.activeFlags.HasFlag(ActiveSkill.Flag.IsSelfTargeted)
                || targetId == id || targetId == InstanceId.NULL);
    }

    private void castOnSelf(SkillId skillId, ActiveSkill skill, SkillInfo info, uint specialAttackId) {
        if (skillId == SkillId.Com_LifestoneRecall) {
            startLifestoneRecall(skill, info, specialAttackId);
            return;
        }
        if (dead || !info.flags.HasFlag(SkillInfo.Flag.IsTrained) || skill.vigorModMult != 0) {
            finishSelfCast(skillId, specialAttackId, ErrorType.Combat_InvalidSkill);
            return;
        }
        double now = world.serverTime.time;
        if (info.flags.HasFlag(SkillInfo.Flag.HasTimeLastUsed)
            && now - info.lastUsedTime < Math.Max(skill.resetTime, skill.minUseTime)) {
            finishSelfCast(skillId, specialAttackId, ErrorType.Combat_InvalidMode);
            return;
        }

        List<(SingletonPkg<Effect> reference, Effect effect)> effects = new();
        HashSet<DataId> seen = new();
        addSelfEffects(skill.userEffects, seen, effects);
        addSelfEffects(skill.targetEffects, seen, effects);
        foreach (AttackHook hook in skill.hooks ?? Enumerable.Empty<AttackHook>()) {
            if (hook.requiredEffect != null && !hasSpellEffect(hook.requiredEffect)) {
                continue;
            }
            if (Random.Shared.NextSingle() < hook.chanceToFire) {
                addSelfEffects(hook.userEffects, seen, effects);
                addSelfEffects(hook.targetEffects, seen, effects);
            }
        }
        if (effects.Count == 0) {
            finishSelfCast(skillId, specialAttackId, ErrorType.Combat_InvalidSkill);
            return;
        }

        // Flat spell costs in the DAT use a negative offset.
        int cost = Math.Max(0, -skill.vigorModAdd);
        if (vigor < cost) {
            finishSelfCast(skillId, specialAttackId, ErrorType.Combat_NotEnoughVigor);
            return;
        }

        vigor -= cost;
        int level = getSkillLevel(skillId);
        foreach (var (reference, effect) in effects) {
            applySelfEffect(reference, effect, level, now);
        }

        BehaviorId behavior = skill.behaviorSelfHit != BehaviorId.Undef ? skill.behaviorSelfHit : skill.behaviorOtherHit;
        if (behavior != BehaviorId.Undef) {
            doBehavior(new() { behaviorId = behavior, timeScale = combatAnimationScale() });
        }
        completeSelfCast(skillId, info, specialAttackId, now);
    }

    private void completeSelfCast(SkillId skillId, SkillInfo info, uint specialAttackId, double now) {
        info.flags |= SkillInfo.Flag.HasTimeLastUsed;
        info.lastUsedTime = now;
        if (player != null) {
            world.playerManager.send(player, new UpdateSkillInfoCEvt { skillInfo = info });
        }
        finishSelfCast(skillId, specialAttackId, ErrorType.None);
    }

    private bool hasSpellEffect(SingletonPkg<Effect> reference) =>
        effectRegistry.effectInfo.Values.Any(record => record.effect?.wstateDid == reference.wstateDid);

    private void addSelfEffects(List<SingletonPkg<Effect>>? refs, HashSet<DataId> seen,
        List<(SingletonPkg<Effect>, Effect)> result) {
        if (refs == null) {
            return;
        }
        foreach (SingletonPkg<Effect> reference in refs) {
            if (!seen.Add(reference.wstateDid) || !world.contentManager.contains(reference.wstateDid)) {
                continue;
            }
            if (world.contentManager.getWeenieState(reference.wstateDid).package is not Effect effect
                || effect.internalFlags.HasFlag(Effect.InternalFlag.IsHarmful)) {
                continue;
            }
            if (effect is InstantVitalEffect vital && supportsInstantVital(vital)
                || effect is QualitiesEffect quality && (supportsCombatSpeed(quality) || supportsDefenseBonus(quality))) {
                result.Add((reference, effect));
            }
        }
    }

    private static bool supportsInstantVital(InstantVitalEffect effect) {
        const InstantVitalEffect.Flag supported = InstantVitalEffect.Flag.IsHealth | InstantVitalEffect.Flag.IsVigor
            | InstantVitalEffect.Flag.IsInitialChangeConstant | InstantVitalEffect.Flag.IsInitialChangeVariable;
        var flags = effect.instantVitalFlags;
        return (flags & ~supported) == 0 && (flags & (InstantVitalEffect.Flag.IsHealth | InstantVitalEffect.Flag.IsVigor)) != 0;
    }

    private static bool supportsCombatSpeed(QualitiesEffect effect) =>
        effect.statList?.Count == 1 && effect.statList[0] == COMBAT_ANIMATION_SCALE
        && effect.parameterizedNumericFlags.HasFlag(ParameterizedNumericEffect.Flag.IsMultiplicative)
        && effect.durationData?.Count > 0 && effect.magData?.Count > 0;

    private static bool supportsDefenseBonus(QualitiesEffect effect) =>
        effect.statList?.Count == 1
        && effect.statList[0] is (SKILL_RATING | (uint)SkillId.Com_Me_MeleeDefense)
            or (SKILL_RATING | (uint)SkillId.Com_Mi_MissileDefense)
            or (SKILL_RATING | (uint)SkillId.Com_Ma_MagicDefense)
        && effect.parameterizedNumericFlags.HasFlag(ParameterizedNumericEffect.Flag.IsAdditive)
        && effect.durationData?.Count > 0 && effect.magData?.Count > 0;

    public float defenseEvadeChance(SkillId defense, float baseChance) {
        float bonus = 0;
        uint stat = SKILL_RATING | (uint)defense;
        foreach (EffectRecord record in effectRegistry.effectInfo.Values) {
            if (record.effect != null && record.appFloat > 0
                && world.serverTime.time - record.timeCast < record.timeout
                && world.contentManager.contains(record.effect.wstateDid)
                && world.contentManager.getWeenieState(record.effect.wstateDid).package is QualitiesEffect effect
                && supportsDefenseBonus(effect) && effect.statList[0] == stat) {
                bonus = Math.Max(bonus, record.appInt);
            }
        }

        // The local combat model uses flat evasion. Convert the extra rating
        // into a bounded bonus without changing the unbuffed chance.
        return baseChance + (1 - baseChance) * bonus / (100 + bonus);
    }

    private void applySelfEffect(SingletonPkg<Effect> reference, Effect effect, int level, double now) {
        if (effect is InstantVitalEffect vital) {
            int change = (int)MathF.Round(spellValue(vital.changeData, level, vital.appValue));
            if (vital.instantVitalFlags.HasFlag(InstantVitalEffect.Flag.IsHealth)) {
                health = Math.Clamp(health + change, 0, healthMax);
            }
            if (vital.instantVitalFlags.HasFlag(InstantVitalEffect.Flag.IsVigor)) {
                vigor = Math.Clamp(vigor + change, 0, vigorMax);
            }
        } else if (effect is QualitiesEffect quality) {
            foreach (EffectId existing in effectRegistry.effectInfo
                .Where(pair => pair.Value.effect?.wstateDid == reference.wstateDid)
                .Select(pair => pair.Key).ToList()) {
                removeEffect(existing);
            }
            float magnitude = spellValue(quality.magData, level, 1);
            addEffect(new() {
                casterId = id,
                timeCast = now,
                timeDemotedFromTopLevel = -1,
                timePromotedToTopLevel = now,
                timeout = spellValue(quality.durationData, level, 0),
                appFloat = magnitude,
                appInt = (int)MathF.Floor(magnitude),
                spellcraft = level,
                effect = reference,
                flags = EffectRecord.Flag.IsSpecifiedTimeout | EffectRecord.Flag.IsTransientEffect,
            });
        }
        if (effect.fxId != FxId.Undef) {
            doFx(effect.fxId, 1);
        }
    }

    private static float spellValue(List<FloatScaleDuple>? points, int level, float fallback) {
        if (points == null || points.Count == 0) {
            return fallback;
        }
        FloatScaleDuple[] sorted = points.OrderBy(point => point.level).ToArray();
        for (int i = 0; i < sorted.Length; i++) {
            FloatScaleDuple upper = sorted[i];
            if (level <= upper.level) {
                if (i == 0) {
                    return upper.value;
                }
                FloatScaleDuple lower = sorted[i - 1];
                return lower.value + (upper.value - lower.value) * (level - lower.level) / (upper.level - lower.level);
            }
        }
        return sorted[^1].value;
    }

    private float combatAnimationScale() {
        float scale = getQ(FloatStat.Physics_CombatAnimScale);
        float modifier = 1;
        foreach (EffectRecord record in effectRegistry.effectInfo.Values) {
            if (record.effect != null && record.appFloat > modifier
                && world.contentManager.getWeenieState(record.effect.wstateDid).package is QualitiesEffect effect
                && supportsCombatSpeed(effect)) {
                modifier = record.appFloat;
            }
        }
        return (scale > 0 ? scale : 1) * modifier;
    }

    private void finishSelfCast(SkillId skillId, uint specialAttackId, ErrorType error) {
        if (player == null) {
            return;
        }
        if (specialAttackId != 0) {
            world.playerManager.send(player, new EndSpecialAttackCEvt { lastSpecialAttackId = specialAttackId });
        }
        if (error != ErrorType.None) {
            world.playerManager.send(player, new AttackErrorCEvt { skillId = skillId, targetId = id, error = error });
        }
    }
}


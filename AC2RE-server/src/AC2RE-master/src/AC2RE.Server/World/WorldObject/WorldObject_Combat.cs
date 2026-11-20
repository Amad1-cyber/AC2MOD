using AC2RE.Definitions;
using System;
using System.Collections.Generic;

namespace AC2RE.Server;

internal partial class WorldObject {

    public bool attacking { get; private set; }

    public void initCombat() {

    }

    public void doAttack(SkillId skillId, InstanceId targetId, uint specialAttackId) {
        if (dead || !world.contentManager.tryGetSkill(skillId, out Skill? skill)
            || skill is not ActiveSkill active || !skillRepo.skills.TryGetValue(skillId, out SkillInfo? skillInfo)) {
            finishSelfCast(skillId, specialAttackId, ErrorType.Combat_InvalidSkill);
            return;
        }
        if (isSelfCast(active, targetId)) {
            castOnSelf(skillId, active, skillInfo, specialAttackId);
            return;
        }

        setAttacking(true);
        InstanceId weaponId = getEquipped(InvLoc.PrimaryHand);
        if (weaponId == InstanceId.NULL) {
            weaponId = getEquipped(InvLoc.SecondaryHand);
        }

        List<StoryHookData> hooks = new();
        int hookCount = active.hooks?.Count ?? 0;
        for (int i = 0; i < hookCount; i++) {
            AttackHook hook = active.hooks![i];
            if (Random.Shared.NextSingle() <= hook.chanceToFire) {
                if (hook.flags.HasFlag(AttackHook.Flag.IsProjectile)) {
                    hooks.Add(new() {
                        type = StoryHookData.HookType.CreateMissiles,
                    });
                    hooks.Add(new() {
                        type = StoryHookData.HookType.FireMissile,
                        targetId = targetId,
                        weaponId = weaponId,
                        animHookNumber = hook.animHookIndex,
                        attackHookNumber = (uint)(hookCount - 1),
                    });
                }
                if (hook is StaticAttackHook staticAttackHook) {
                    float damage = staticAttackHook.addMod;
                    CombatResultType result = resolveAttackDamage(skillId, targetId, ref damage);
                    hooks.Add(new() {
                        type = StoryHookData.HookType.Damage,
                        targetId = targetId,
                        weaponId = weaponId,
                        attackResult = result,
                        animHookNumber = hook.animHookIndex,
                        attackHookNumber = (uint)(hookCount - 1),
                        targetHealthChange = -(int)damage,
                    });
                } else if (hook is LinearAttackHook linearAttackHook) {
                    int level = getSkillLevel(skillId);
                    float damage = 0.0f;
                    if (linearAttackHook.addDmgData.Count > 1) {
                        float damagePerLevel = (linearAttackHook.addDmgData[0].value - linearAttackHook.addDmgData[1].value) / (linearAttackHook.addDmgData[0].level - linearAttackHook.addDmgData[1].level);
                        damage = linearAttackHook.addDmgData[1].value + damagePerLevel * (level - 1);
                    }
                    CombatResultType result = resolveAttackDamage(skillId, targetId, ref damage);
                    hooks.Add(new() {
                        type = StoryHookData.HookType.Damage,
                        targetId = targetId,
                        weaponId = weaponId,
                        attackResult = result,
                        animHookNumber = hook.animHookIndex,
                        attackHookNumber = (uint)(hookCount - 1),
                        targetHealthChange = -(int)damage,
                    });
                } else {
                    hooks.Add(new() {
                        targetId = targetId,
                        attackResult = CombatResultType.Hit,
                    });
                }
            }
        }

        doStory(new() {
            attackerId = id,
            attackerBehavior = new() {
                behaviorId = active.behaviorOtherHit,
                timeScale = combatAnimationScale(),
            },
            clientAttackContextId = player != null ? ++player.attackNum : 0,
            skillId = skillId,
            hooks = hooks,
        });

        skillInfo.flags |= SkillInfo.Flag.HasTimeLastUsed;
        skillInfo.lastUsedTime = world.serverTime.time;
        if (player != null) {
            world.playerManager.send(player, new UpdateSkillInfoCEvt {
                skillInfo = skillInfo,
            });
        }
    }

    private CombatResultType resolveAttackDamage(SkillId skillId, InstanceId targetId, ref float damage) {
        if (!world.objectManager.tryGet(targetId, out WorldObject? target)) {
            return CombatResultType.Hit;
        }

        if (target.player != null && target != this && damage > 0 && target.effectRegistry.effectInfo.Count > 0) {
            // Skill IDs identify the combat discipline independently of the equipped weapon.
            string skill = skillId.ToString();
            SkillId defense = skill.Contains("_Ma_", StringComparison.Ordinal) ? SkillId.Com_Ma_MagicDefense
                : skill.Contains("_Mi_", StringComparison.Ordinal) ? SkillId.Com_Mi_MissileDefense
                : skill.Contains("_Me_", StringComparison.Ordinal) ? SkillId.Com_Me_MeleeDefense
                : SkillId.Undef;
            if (defense != SkillId.Undef && Random.Shared.NextSingle() < target.defenseEvadeChance(defense, 0)) {
                damage = 0;
                return CombatResultType.Evade;
            }
        }

        damage = target.applyDamage(damage, this);
        return CombatResultType.Hit;
    }

    public void setAttacking(bool attacking) {
        if (player == null || this.attacking == attacking) {
            return;
        }

        this.attacking = attacking;
        world.playerManager.send(player, new UpdateAttackStateCEvt() {
            attacking = attacking,
        });

        syncMode();
    }

    public float applyDamage(float damage, WorldObject? attacker) {
        // MOD2: things without vitals (NPCs, scenery) and things that are already dead can't be hurt
        if (healthMax <= 0 || dead) {
            return 0.0f;
        }

        if (damage >= 1) {
            cancelLifestoneRecall("Taking damage interrupted your Lifestone Recall.");
        }
        health = Math.Max(health - (int)damage, 0);

        // (was integer division, which made the blood effect all-or-nothing; the real server sent e.g. 0.4 at 15/25 health)
        doFx(FxId.BloodyDamaged, 1.0f - ((float)health / healthMax));

        // MOD2: after every hit the real server told the attacker how its target was doing (the target panel's health bar)
        if (player == null && attacker?.player != null) {
            world.playerManager.send(attacker.player, new UpdateSelectionInfoCEvt {
                selectionId = id,
                info = new() {
                    type = SelectionInfo.InfoType.Agent,
                    curHealth = health,
                    maxHealth = healthMax,
                    curVigor = vigor,
                    maxVigor = vigorMax,
                },
            });
        }

        // MOD2: ... and the identify panel they have open on it shows its new health (the real server refreshed it after every hit)
        if (player == null && attacker?.player != null && attacker.player.examinedId == id) {
            world.examination.push(attacker.player, this);
        }

        // MOD2: a hurt creature turns on whoever hit it
        if (player == null) {
            world.monsterAi.onDamaged(this, attacker);
        }

        if (health <= 0 && !dead) {
            dead = true;
            selectable = false;

            doBehavior(new() {
                behaviorId = BehaviorId.death,
                modeId = ModeId.dead,
                cameraParent = true,
                cameraTarget = true,
                cameraHold = true,
            });
            if (player != null) {
                if (attacker != null) {
                    world.playerManager.send(player, new DisplayDeathMessageCEvt {
                        lastAttackerName = attacker.name,
                    });
                }
                world.playerManager.send(player, new SetMovementFrozenCEvt {
                    frozen = true,
                });
                // TODO: Add 10 second delay before death state is updated
                world.playerManager.send(player, new UpdateDeathStateCEvt {
                    dead = dead,
                });
            }
            if (attacker != null) {
                if (attacker.player != null) {
                    world.playerManager.send(attacker.player, new DisplayKillingMessageCEvt {
                        defenderName = name,
                    });
                }
                world.playerManager.sendAllVisible(id, new InterpCEventVisualMsg {
                    senderIdWithStamp = getInstanceIdWithStamp(++physics.visualOrderStamp),
                    netEvent = new AnnounceWhoKilledMeCEvt {
                        killerName = attacker.name,
                        killerId = attacker.id,
                    },
                });
            }

            // MOD2: a creature that died drops loot, is removed after a while and respawns later (see MonsterAi)
            if (player == null) {
                world.monsterAi.onKilled(this, attacker);
            }
        }

        return damage;
    }

    public PositionOffset? lifestoneBind { get; set; }

    public void resurrect() {
        if (!dead) {
            Logs.GENERAL.warn("Attempt to resurrect when not dead", "objId", id);
            return;
        }

        health = healthMax;
        vigor = vigorMax;
        dead = false;
        selectable = true;

        if (player != null) {
            addEffect(new() {
                timeDemotedFromTopLevel = -1.0,
                timeCast = world.serverTime.time,
                casterId = player.characterId,
                timeout = 180.0f,
                appFloat = 0.0f,
                spellcraft = 0.0f,
                appInt = 0,
                pk = false,
                appPackage = null,
                timePromotedToTopLevel = -1.0,
                effect = new() {
                    wstateDid = new(0x70000973),
                },
                actingForWhomId = default,
                skillDid = default,
                fromItemId = player.characterId,
                flags = EffectRecord.Flag.IsSpecifiedTimeout | EffectRecord.Flag.IsTransientEffect,
                durabilityLevel = 0,
                relatedEffectId = EffectId.NULL,
                categories = 1,
                maxDurabilityLevel = 0,
            });
            world.playerManager.send(player, new SetAnimationFrozenCEvt {
                frozen = true,
            });
            world.playerManager.send(player, new EnterPortalSpaceCEvt());
            if (lifestoneBind is PositionOffset destination) {
                teleport(player, destination);
            }
            setMode(ModeId.peace);
            world.playerManager.send(player, new ExitPortalSpaceCEvt());
            world.playerManager.send(player, new SetAnimationFrozenCEvt {
                frozen = false,
            });
            world.playerManager.send(player, new UpdateDeathStateCEvt {
                dead = dead,
            });
        }

        doFx(FxId.BloodyDamaged, 0.0f);
        doFx(FxId.Lifestone_Prot, 1.0f);
        doFx(FxId.Enter_World, 1.0f);

        stopBehavior(BehaviorId.death);

        doBehavior(new() {
            behaviorId = BehaviorId.Revive,
            cameraRestore = true,
        });

        if (player != null) {
            world.playerManager.send(player, new SetMovementFrozenCEvt {
                frozen = false,
            });
            stopFx(FxId.Enter_World);
        }
    }
}

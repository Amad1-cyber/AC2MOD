using AC2RE.Definitions;
using System;
using System.Numerics;

namespace AC2RE.Server;

internal class UsageMessageProcessor : BaseMessageProcessor {

    public UsageMessageProcessor(World world) : base(world) {

    }

    public override bool processMessage(ClientConnection client, Player player, INetMessage genericMsg) {
        switch (genericMsg.opcode) {
            case MessageOpcode.Interp__InterpSEvent: {
                    InterpSEventMsg msg = (InterpSEventMsg)genericMsg;
                    if (msg.netEvent.funcId == ServerEventFunctionId.Usage__TryToUseItem) {
                        TryToUseItemSEvt sEvent = (TryToUseItemSEvt)msg.netEvent;
                        if (tryGetCharacter(player, out WorldObject? character) && world.objectManager.tryGet(sEvent.usageDesc.itemId, out WorldObject? item)) {
                            if (item.containerId == character.id && PortalTable.tryGet(sEvent.usageDesc.targetId.id, out PortalTable.Destination portal)
                                && portal.requiredKey != DataId.NULL && item.entityDid == portal.requiredKey) {
                                usePortalKey(player, character, item, sEvent.usageDesc, portal);
                            } else if (item.containerId != character.id && item.takeable) {
                                InvMoveDesc moveDesc = new() {
                                    itemId = item.id,
                                    splitItemId = item.id,
                                    fromId = item.containerId,
                                    actualFromContainerId = item.containerId,
                                    targetId = character.id,
                                    actualTargetContainerId = character.id,
                                    mergeContainerId = character.id,
                                    itemVisualDescDid = item.physicsEntityDid,
                                    splitItemEntityDid = item.entityDid,
                                    allItemUnitsTaken = true,
                                    doContain = true,
                                    moveType = 12,
                                    itemAprDid = item.pileAppearanceDid,
                                };
                                WorldObject.moveItem(world, moveDesc, player);
                            } else {
                                useWorldObject(player, character, item, sEvent.usageDesc);
                            }
                        } else {
                            fail(player, sEvent.usageDesc, ErrorType.Usage_GeneralError);
                        }
                    } else {
                        return false;
                    }
                    break;
                }
            default:
                return false;
        }
        return true;
    }

    private void done(Player player, UsageDesc usageDesc) {
        send(player, new TryToUseItemDoneCEvt {
            usageDesc = usageDesc,
        });
    }

    private void fail(Player player, UsageDesc usageDesc, ErrorType error) {
        usageDesc.error = error;
        done(player, usageDesc);
    }

    private string displayName(WorldObject obj) {
        try {
            return obj.name != null ? world.contentManager.getString(obj.name) : "it";
        } catch (Exception) {
            return "it";
        }
    }

    /// <summary>MOD2: use a world object. Portals (and the keeper NPC beside a Blue portal) travel using portals.csv.</summary>
    private void useWorldObject(Player player, WorldObject character, WorldObject item, UsageDesc usageDesc) {
        // A mapped portal can also be a shard with no usage action in the client data.
        if (PortalTable.tryGet(item.id.id, out PortalTable.Destination dest)
            || (item.packageType == PackageType.NPCImmobileTemplate && PortalTable.tryGet(item.id.id + 1, out dest))) {
            if (dest.requiredKey == DataId.NULL) {
                travel(player, usageDesc, dest);
            } else {
                sendText(player, $"Use the matching glyph on the {displayName(item)}.");
                done(player, usageDesc);
            }
            return;
        }

        DataId usageActionDid = DataId.NULL;
        try {
            WState weenieState = world.contentManager.getWeenieStateFromEntityDid(item.entityDid);
            if (weenieState.package is gmEntity entity) {
                usageActionDid = entity.usageAction?.wstateDid ?? DataId.NULL;
            }
        } catch (Exception e) {
            Logs.GENERAL.warn(e, "Could not read usage data", "entityDid", item.entityDid);
        }

        if (usageActionDid == DataId.NULL) {
            if (item.selectable && !item.takeable) {
                done(player, usageDesc);
                forceExamine(player, item);
            } else {
                fail(player, usageDesc, ErrorType.Usage_CantUse);
            }
            return;
        }

        PackageType type = world.contentManager.getWeenieState(usageActionDid).packageType;
        string name = displayName(item);

        if (PackageTypes.isPackageType(type, PackageType.EquippableUsageAction) && item.containerId == character.id) {
            WorldObject.toggleAutoEquip(world, character, item, player);
            return;
        }

        if (type == PackageType.PortalUsageAction) {
            sendText(player, $"The {name} shimmers, but its destination was never recorded, so it can't take you anywhere yet.");
            done(player, usageDesc);
        } else if (type == PackageType.MonsterUsageAction) {
            character.setAttacking(true);
            sendText(player, $"You ready yourself against the {name}.");
            done(player, usageDesc);
        } else if (type == PackageType.LifestoneUsageAction) {
            if (character.dead || !item.inWorld || item.containerId != InstanceId.NULL) {
                fail(player, usageDesc, ErrorType.Usage_CantUse);
            } else if (!isWithinUsageRange(character, item)) {
                fail(player, usageDesc, ErrorType.Usage_InteractionTargetTooFarAway);
            } else {
                // Keep the player's standing point beside the stone for a safe arrival.
                character.lifestoneBind = character.offset;
                usageDesc.successText = new(new(0x25000014), new(0x1E2D6E94));
                usageDesc.error = ErrorType.None;
                done(player, usageDesc);
            }
        } else if (type == PackageType.NPCUsageAction || !item.takeable) {
            // The real server answered "use" on NPCs, teaching stones, signs and the like by opening their identify panel
            // (a ForceExamineItem right after the use was done - seen throughout the capture)
            done(player, usageDesc);
            forceExamine(player, item);
        } else {
            fail(player, usageDesc, ErrorType.Usage_CantUse);
        }
    }

    private void usePortalKey(Player player, WorldObject character, WorldObject item, UsageDesc usageDesc, PortalTable.Destination dest) {
        if (character.dead || !character.contains(item.id)
            || !world.objectManager.tryGet(usageDesc.targetId, out WorldObject? portal)
            || !portal.inWorld || portal.containerId != InstanceId.NULL) {
            fail(player, usageDesc, ErrorType.Usage_CantUse);
            return;
        }

        if (!isWithinUsageRange(character, portal)) {
            fail(player, usageDesc, ErrorType.Usage_InteractionTargetTooFarAway);
            return;
        }

        if (item.quantity > 1) {
            item.quantity--;
        } else {
            item.setContainer(null);
            item.delete();
            if (player.visibleObjectIds.Remove(item.id)) {
                send(player, new DestroyObjectMsg { idWithStamp = item.getInstanceIdWithStamp() });
            }
            send(player, new DiscreteDestroyContainedItemCEvt { itemId = item.id });
        }
        travel(player, usageDesc, dest);
    }

    private static bool isWithinUsageRange(WorldObject character, WorldObject target) {
        return character.landblockId == target.landblockId
            && Vector3.DistanceSquared(character.offset.offset, target.offset.offset) <= 36.0f;
    }

    private void travel(Player player, UsageDesc usageDesc, PortalTable.Destination dest) {
        send(player, new EnterPortalSpaceCEvt());
        teleport(player, new(new(dest.cellId), dest.offset));
        send(player, new ExitPortalSpaceCEvt());
        done(player, usageDesc);
    }

    private void forceExamine(Player player, WorldObject item) {
        send(player, new ForceExamineItemCEvt {
            targetId = item.id,
        });
    }
}

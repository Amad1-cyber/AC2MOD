using AC2RE.Definitions;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AC2RE.Server;

internal class CombatMessageProcessor : BaseMessageProcessor {

    private int toggleCounter = 0;
    private EffectId testEffectId;

    public CombatMessageProcessor(World world) : base(world) {

    }

    public override bool processMessage(ClientConnection client, Player player, INetMessage genericMsg) {
        switch (genericMsg.opcode) {
            case MessageOpcode.Interp__InterpSEvent: {
                    InterpSEventMsg msg = (InterpSEventMsg)genericMsg;
                    // MOD2: upstream had a developer test hook here (spawned a "TestObj" and toggled an effect every time the
                    // attack-mode button was pressed). Pressing it now simply enters attack mode.
                    if (msg.netEvent.funcId == ServerEventFunctionId.Combat__StartAttack) {
                        if (tryGetCharacter(player, out WorldObject? character)) {
                            character.setAttacking(true);
                        }
                    } else if (msg.netEvent.funcId == ServerEventFunctionId.Combat__StopAttack) {
                        if (tryGetCharacter(player, out WorldObject? character)) {
                            character.setAttacking(false);
                        }
                    } else if (msg.netEvent.funcId == ServerEventFunctionId.Combat__DoAttack) {
                        DoAttackSEvt sEvent = (DoAttackSEvt)msg.netEvent;
                        if (tryGetCharacter(player, out WorldObject? character)) {
                            character.doAttack(sEvent.skillId, sEvent.targetId, sEvent.specialAttackId);
                        }
                    } else if (msg.netEvent.funcId == ServerEventFunctionId.Combat__SetNextTarget || msg.netEvent.funcId == ServerEventFunctionId.Combat__StopAttacking) {
                        // MOD2: the client sends these constantly while fighting; there is nothing to do (they only filled the log with
                        // "Unhandled message")
                    } else if (msg.netEvent.funcId == ServerEventFunctionId.Death__RequestResurrect) {
                        if (tryGetCharacter(player, out WorldObject? character)) {
                            character.resurrect();
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
}

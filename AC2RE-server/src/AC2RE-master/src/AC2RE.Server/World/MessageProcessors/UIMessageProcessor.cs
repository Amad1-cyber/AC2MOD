using AC2RE.Definitions;

namespace AC2RE.Server;

internal class UIMessageProcessor : BaseMessageProcessor {

    public UIMessageProcessor(World world) : base(world) {

    }

    public override bool processMessage(ClientConnection client, Player player, INetMessage genericMsg) {
        // MOD2: messages the client sends constantly that need no server reaction (stop the "Unhandled message" noise)
        if (genericMsg is CLookAtMsg || genericMsg is ClientSceneRenderingCompleteMsg) {
            return true;
        }

        switch (genericMsg.opcode) {
            case MessageOpcode.Interp__InterpSEvent: {
                    InterpSEventMsg msg = (InterpSEventMsg)genericMsg;
                    if (msg.netEvent.funcId == ServerEventFunctionId.Player__SetSelection) {
                        // MOD2: answer a selection with the target's vitals so the client's target panel / "selected" state works
                        SetSelectionSEvt selEvent = (SetSelectionSEvt)msg.netEvent;
                        if (world.objectManager.tryGet(selEvent.selectionId, out WorldObject? target) && target.healthMax > 0) {
                            send(player, new UpdateSelectionInfoCEvt {
                                selectionId = selEvent.selectionId,
                                info = new() {
                                    type = SelectionInfo.InfoType.Agent,
                                    curHealth = target.health,
                                    maxHealth = target.healthMax,
                                    curVigor = target.vigor,
                                    maxVigor = target.vigorMax,
                                },
                            });
                        }
                    } else if (msg.netEvent.funcId == ServerEventFunctionId.Player__UploadUISettings) {
                        // MOD2: window layout the client uploads; not stored (it used to be logged as unhandled)
                    } else if (msg.netEvent.funcId == ServerEventFunctionId.Allegiance__QueryAllegianceProfile) {
                        // no allegiance system yet: nothing to answer
                    } else if (msg.netEvent.funcId == ServerEventFunctionId.Player__SetShortcut) {
                        SetShortcutSEvt sEvent = (SetShortcutSEvt)msg.netEvent;
                        if (sEvent.index >= player.dbCharacter.shortcuts.Length) {
                            sendText(player, "Shortcut index out of range", TextType.Error);
                            return true;
                        }

                        if ((sEvent.shortcut.valString?.Length ?? 0) > 255) {
                            sendText(player, "Shortcut alias too long", TextType.Error);
                            return true;
                        }

                        player.dbCharacter.shortcuts[(int)sEvent.index] = sEvent.shortcut;
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

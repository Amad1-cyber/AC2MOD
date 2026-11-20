using AC2RE.Definitions;

namespace AC2RE.Server;

/// <summary>
/// MOD2: answers the client's "examine / identify" requests with the panel the real server built (see ExaminationBuilder).
///
/// Upstream's version answered with a dump of every raw property and a made-up "12345" for anything else. The real server only
/// answered requests about an object it knew (the client also sends empty "close the panel" requests that got no answer).
/// </summary>
internal class ExaminationMessageProcessor : BaseMessageProcessor {

    public ExaminationMessageProcessor(World world) : base(world) {

    }

    public override bool processMessage(ClientConnection client, Player player, INetMessage genericMsg) {
        switch (genericMsg.opcode) {
            case MessageOpcode.Interp__InterpSEvent: {
                    InterpSEventMsg msg = (InterpSEventMsg)genericMsg;
                    if (msg.netEvent.funcId == ServerEventFunctionId.Examination__QueryExaminationProfile) {
                        QueryExaminationProfileSEvt sEvent = (QueryExaminationProfileSEvt)msg.netEvent;
                        ExaminationProfile? profile = world.examination.build(sEvent.request);
                        if (profile != null) {
                            player.examinedId = sEvent.request.dataId;
                            send(player, new UpdateExaminationProfileCEvt {
                                profile = profile,
                            });
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

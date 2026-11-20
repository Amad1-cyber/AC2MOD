using AC2RE.Definitions;
using System.Linq;

namespace AC2RE.Server;

internal partial class WorldObject {

    public readonly EffectRegistry effectRegistry = new() {
        appliedFx = new(),
        effectInfo = new(),
        appliedAppearances = new(),
    };

    public void initEffect() {

    }

    public EffectId addEffect(EffectRecord effectRecord) {
        EffectId effectId = new(++effectRegistry.effectIdCounter);
        effectRecord.effectId = effectId;

        effectRegistry.effectInfo[effectId] = effectRecord;

        if (player != null) {
            world.playerManager.send(player, new ClientAddEffectCEvt {
                effectRecord = effectRecord,
                effectId = effectId,
            });
        }

        return effectId;
    }

    private void expireEffects() {
        if (effectRegistry.effectInfo.Count == 0) {
            return;
        }
        double now = world.serverTime.time;
        foreach (EffectId id in effectRegistry.effectInfo
            .Where(pair => pair.Value.flags.HasFlag(EffectRecord.Flag.IsSpecifiedTimeout)
                && now - pair.Value.timeCast >= pair.Value.timeout)
            .Select(pair => pair.Key).ToList()) {
            removeEffect(id);
        }
    }

    public void removeEffect(EffectId effectId) {
        effectRegistry.effectInfo.Remove(effectId);

        if (player != null) {
            world.playerManager.send(player, new ClientRemoveEffectCEvt {
                effectId = effectId,
            });
        }
    }
}

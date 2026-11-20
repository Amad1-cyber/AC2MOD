using AC2RE.Definitions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AC2RE.Server;

/// <summary>MOD2: portal destinations observed in real-server captures (portals.csv in the server's working directory).</summary>
internal static class PortalTable {

    public readonly record struct Destination(uint cellId, Vector3 offset, DataId requiredKey);

    private static readonly Dictionary<ulong, Destination> DESTINATIONS = new();
    private static readonly Dictionary<ulong, DataId> KEYS_BY_LOOT_SOURCE = new();

    public static void load(string path = "portals.csv") {
        DESTINATIONS.Clear();
        KEYS_BY_LOOT_SOURCE.Clear();
        if (!File.Exists(path)) {
            Logs.STATUS.warn("No portal table found", "path", path);
            return;
        }

        foreach (string rawLine in File.ReadAllLines(path)) {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) {
                continue;
            }
            string[] parts = line.Split(',');
            try {
                ulong id = Convert.ToUInt64(parts[0].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
                uint cell = Convert.ToUInt32(parts[1].Trim(), 16);
                Vector3 offset = new(
                    float.Parse(parts[2], CultureInfo.InvariantCulture),
                    float.Parse(parts[3], CultureInfo.InvariantCulture),
                    float.Parse(parts[4], CultureInfo.InvariantCulture));
                if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z)) {
                    throw new FormatException("Portal offset must have finite coordinates.");
                }

                // The optional columns identify an inventory key and the map object that drops it.
                DataId requiredKey = DataId.NULL;
                if (parts.Length >= 8) {
                    requiredKey = new(Convert.ToUInt32(parts[6].Trim(), 16));
                    ulong lootSource = Convert.ToUInt64(parts[7].Trim(), 16);
                    KEYS_BY_LOOT_SOURCE.Add(lootSource, requiredKey);
                } else if (parts.Length == 7) {
                    throw new FormatException("A portal key also needs a loot source.");
                }
                DESTINATIONS[id] = new Destination(cell, offset, requiredKey);
            } catch (Exception e) {
                Logs.STATUS.warn(e, "Bad portal table line", "line", line);
            }
        }
        Logs.STATUS.info("Loaded portal destinations", "count", DESTINATIONS.Count);
    }

    public static bool tryGet(ulong objectId, out Destination destination) {
        return DESTINATIONS.TryGetValue(objectId, out destination);
    }

    public static bool tryGetLootKey(InstanceId source, out DataId key) {
        return KEYS_BY_LOOT_SOURCE.TryGetValue(source.id, out key);
    }
}

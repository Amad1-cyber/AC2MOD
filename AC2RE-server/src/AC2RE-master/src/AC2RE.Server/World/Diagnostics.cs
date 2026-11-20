using AC2RE.Definitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AC2RE.Server;

/// <summary>
/// MOD2: one-shot diagnostics, run at server start when a flag file exists in the server folder (the flag is deleted first):
///   diag.flag  -> logs\diag.txt: every property of some sample items, and what the identification ("examine") panel generates for them.
/// Nothing it creates is saved.
/// </summary>
internal static class Diagnostics {

    private const string FLAG = "diag.flag";
    private static bool done;

    // Items whose real identification panel is in the capture (so the output can be compared): staff, hatchet, board, gauntlets...
    private static readonly uint[] SAMPLE_ITEMS = {
        0x4700051D, 0x47001D08, 0x47001D15, 0x47000DB3, 0x47000D72, 0x47001C1A, 0x4700215D, 0x47000D79,
        0x47000E79, 0x47000E04, 0x47000DF4, 0x4700051F, 0x4700051E, 0x47000520, 0x4700051C, 0x4700051B,
    };

    public static void runOnce(World world) {
        if (done) {
            return;
        }
        done = true;
        if (!File.Exists(FLAG)) {
            return;
        }
        File.Delete(FLAG);

        using StreamWriter w = new(Path.Combine("logs", "diag.txt")) { AutoFlush = true };
        w.WriteLine($"diagnostics {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (uint did in SAMPLE_ITEMS) {
            try {
                WorldObject item = world.objectManager.create(InstanceId.IdType.Dynamic, new DataId(did), DataId.NULL, false);
                w.WriteLine();
                w.WriteLine($"=== item entity 0x{did:X8}  package={item.packageType}  name='{nameOf(world, item)}'");
                try {
                    WState state = world.contentManager.getWeenieStateFromEntityDid(item.entityDid);
                    w.WriteLine($"    weenie package class: {state.package?.GetType().Name}  packageType={state.packageType}" + (state.package is WeaponTemplate wt ? $"  balanceLevel={wt.balanceLevel}" : ""));
                } catch (Exception e) {
                    w.WriteLine($"    weenie package: {e.GetType().Name}");
                }
                w.WriteLine($"    preferredInvLoc={item.preferredInvLoc} validInvLocs={item.validInvLocs} primaryHoldLoc={item.primaryHoldLoc} holdOrientation={item.holdOrientation} implementType={item.implementType} singleWeaponMode={item.singleWeaponMode} withShieldMode={item.withShieldMode} dualWieldMode={item.dualWieldMode}");
                w.WriteLine($"    icon=0x{item.iconDid.id:X8} physicsEntityDid=0x{item.physicsEntityDid.id:X8} scale={item.scale} value={item.value} durability={item.durability}/{item.durabilityMax}");
                dump(w, "ints", item.qualities.ints?.Select(kv => $"{kv.Key}={kv.Value}"));
                dump(w, "floats", item.qualities.floats?.Select(kv => $"{kv.Key}={kv.Value}"));
                dump(w, "bools", item.qualities.bools?.Select(kv => $"{kv.Key}={kv.Value}"));
                dump(w, "strings", item.qualities.strings?.Select(kv => $"{kv.Key}='{kv.Value}'"));
                dump(w, "dids", item.qualities.dids?.Select(kv => $"{kv.Key}=0x{kv.Value.id:X8}"));
                dump(w, "ids", item.qualities.ids?.Select(kv => $"{kv.Key}={kv.Value}"));
                dump(w, "stringInfos", item.qualities.stringInfos?.Select(kv => $"{kv.Key}={describe(world, kv.Value)}"));
                dump(w, "longs", item.qualities.longs?.Select(kv => $"{kv.Key}={kv.Value}"));
                dump(w, "packageIds", item.qualities.packageIds?.Select(kv => $"{kv.Key}={kv.Value}"));
                item.delete();
            } catch (Exception e) {
                w.WriteLine($"=== item entity 0x{did:X8}: FAILED {e.GetType().Name}: {e.Message}");
            }
        }

        // Creatures from the capture, with the real values to compare: Drudge Digger (0x470023B3) level 1 / 25 health,
        // Vermin Pilferer (0x47002D27) level 2 / 52 health, Crawlspace Vermin (0x47002D52) level 4 / 81 health
        foreach (uint did in SAMPLE_CREATURES) {
            try {
                WorldObject c = world.objectManager.create(InstanceId.IdType.Dynamic, new DataId(did), DataId.NULL, false);
                w.WriteLine();
                w.WriteLine($"=== creature entity 0x{did:X8}  package={c.packageType}  name={nameOf(world, c)}  level={c.level} health={c.health}/{c.healthMax} vigor={c.vigor}/{c.vigorMax} scale={c.scale}");
                dump(w, "ints", c.qualities.ints?.Where(kv => kv.Key is not (IntStat.EtherealPhysicsTypeLow or IntStat.EtherealMovementTypeLow or IntStat.EtherealPlacementTypeLow)).Select(kv => $"{kv.Key}={kv.Value}"));
                dump(w, "floats", c.qualities.floats?.Select(kv => $"{kv.Key}={kv.Value}"));
                dump(w, "stringInfos", c.qualities.stringInfos?.Select(kv => $"{kv.Key}={describe(world, kv.Value)}"));
                c.delete();
            } catch (Exception e) {
                w.WriteLine($"=== creature entity 0x{did:X8}: FAILED {e.GetType().Name}: {e.Message}");
            }
        }

        // The identify panel for a few kinds of things, and the level system on a stand-in character
        try {
            w.WriteLine();
            w.WriteLine("################ IDENTIFY PANEL PREVIEW");
            foreach (uint did in new uint[] { 0x47001D08, 0x47001D15, 0x47000DB3, 0x4700051D, 0x47001C1A, 0x47000520, 0x470023B3, 0x47002D27, 0x470014EC, 0x47002E09, 0x47002DFD, 0x4700113A, 0x4700114D }) {
                WorldObject o = world.objectManager.create(InstanceId.IdType.Dynamic, new DataId(did), DataId.NULL, false);
                if (o.healthMax <= 0 && o.getQ(IntStat.Health_RawLevel) > 0) {
                    o.healthMax = o.health = o.getQ(IntStat.Health_RawLevel);
                    o.vigorMax = o.vigor = o.getQ(IntStat.Vigor_RawLevel);
                }
                preview(world, w, o);
                o.delete();
            }

            w.WriteLine();
            w.WriteLine("################ LEVEL SYSTEM TEST (stand-in character)");
            WorldObject hero = world.objectManager.create(InstanceId.IdType.Dynamic, new DataId(0x4700051C), DataId.NULL, false);
            hero.xp = 902;
            hero.xpAvailable = 454;
            hero.level = 7;
            hero.healthMax = 100;
            hero.health = 86;
            hero.vigorMax = 100;
            hero.vigor = 100;
            LevelSystem ls = world.levelSystem;
            w.WriteLine($"table: max level {ls.maxLevel}; XP needed: L2={ls.experienceForLevel(2)} L5={ls.experienceForLevel(5)} L7={ls.experienceForLevel(7)} L8={ls.experienceForLevel(8)} L9={ls.experienceForLevel(9)} L10={ls.experienceForLevel(10)}");
            w.WriteLine($"capture check: 271 XP -> level {ls.levelForExperience(271)} (capture: 4); 287 XP -> level {ls.levelForExperience(287)} (capture: 5)");
            w.WriteLine($"before: level {hero.level}, xp {hero.xp}, health {hero.health}/{hero.healthMax}, vigor {hero.vigor}/{hero.vigorMax}, credits {hero.skillRepo.skillCredits}, next level in {ls.experienceToNextLevel(hero)}");
            ls.checkLevelUp(hero);
            w.WriteLine($"after login-style check at 902 xp: level {hero.level}, health {hero.health}/{hero.healthMax} (max follows the table, current kept)");
            ls.awardExperience(hero, 100);
            w.WriteLine($"after +100 (xp {hero.xp}): level {hero.level}, health {hero.health}/{hero.healthMax}, vigor {hero.vigor}/{hero.vigorMax}, credits {hero.skillRepo.skillCredits}, available {hero.xpAvailable}, next level in {ls.experienceToNextLevel(hero)}");
            ls.awardExperience(hero, 5000);
            w.WriteLine($"after +5000 (xp {hero.xp}): level {hero.level}, health {hero.health}/{hero.healthMax}, vigor {hero.vigor}/{hero.vigorMax}, credits {hero.skillRepo.skillCredits}, next level in {ls.experienceToNextLevel(hero)}");
            hero.delete();
        } catch (Exception e) {
            w.WriteLine($"preview/level test FAILED: {e}");
        }

        w.WriteLine();
        w.WriteLine("diagnostics finished");
    }

    /// <summary>Print what the identify panel would show for an object: one line per display node, with the client's own text resolved.</summary>
    private static void preview(World world, StreamWriter w, WorldObject o) {
        ExaminationProfile? profile = world.examination.build(new ExaminationRequest { type = ExaminationRequest.RequestType.Item, dataId = o.id, unk3 = 1023 });
        w.WriteLine();
        w.WriteLine($"--- identify 0x{o.entityDid.id:X8} '{describe(world, o.name)}' package={o.packageType}: " + (profile == null ? "NO ANSWER" : $"{profile.nodes.Count} nodes, sections (unk1) = {profile.unk1}"));
        if (profile == null) {
            return;
        }
        foreach (ExaminationDataNode n in profile.nodes) {
            string value = n.type switch {
                ExaminationDataNode.DataType.String => resolve(world, n.valString),
                ExaminationDataNode.DataType.Int => n.valInt.ToString(),
                ExaminationDataNode.DataType.LongInt => n.valLongInt.ToString(),
                ExaminationDataNode.DataType.Image => $"0x{n.valDataID.id:X8}",
                ExaminationDataNode.DataType.Tab => "",
                _ => "",
            };
            w.WriteLine($"      [{n.order,2}] {n.type,-8} {(n.appearanceId == 3193660691 ? "title" : n.appearanceId == 868106293 ? "label" : n.appearanceId.ToString()),-6} {value}");
        }

        // Make sure every node survives the wire format (the client reads it back with the same code)
        using MemoryStream stream = new();
        using AC2Writer writer = new(stream);
        profile.write(writer);
        writer.Flush();
        w.WriteLine($"      (serialized: {stream.Length} bytes)");
    }

    private static string resolve(World world, StringInfo? s) {
        if (s == null) {
            return "null";
        }
        string text = s.literalValue != null ? $"\"{s.literalValue}\"" : describe(world, s);
        if (s.variables != null && s.variables.Count > 0) {
            text += "  {" + string.Join(", ", s.variables.Select(kv => $"{(uint)kv.Key}=" + (kv.Value.type == StringInfoData.DataType.StringInfo ? resolve(world, kv.Value.valString) : kv.Value.type == StringInfoData.DataType.LInt ? kv.Value.valLong.ToString() : kv.Value.valInt.ToString()))) + "}";
        }
        return text;
    }

    private static readonly uint[] SAMPLE_CREATURES = {
        0x470023B3, 0x47002D27, 0x47002D52, 0x47002D22, 0x47002D2F, 0x47002224, 0x47002D34, 0x47002AF9, 0x470021BE, 0x470014EC, 0x470023B5,
    };

    private static void dump(StreamWriter w, string title, IEnumerable<string>? entries) {
        if (entries == null) {
            return;
        }
        List<string> list = entries.ToList();
        if (list.Count > 0) {
            w.WriteLine($"    {title}: " + string.Join(", ", list));
        }
    }

    private static string describe(World world, StringInfo? s) {
        if (s == null) {
            return "null";
        }
        if (s.literalValue != null) {
            return $"\"{s.literalValue}\"";
        }
        try {
            return $"T(0x{s.tableDid.id:X8}:0x{s.stringId.id:X8})=\"{world.contentManager.getString(s)}\"";
        } catch (Exception) {
            return $"T(0x{s.tableDid.id:X8}:0x{s.stringId.id:X8})";
        }
    }

    private static string nameOf(World world, WorldObject obj) => describe(world, obj.name);
}

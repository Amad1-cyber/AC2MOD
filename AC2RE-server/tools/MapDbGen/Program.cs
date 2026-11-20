using AC2RE.Definitions;
using AC2RE.PacketTool;
using AC2RE.Utils;
using System.Linq;
using AC2RE.PacketTool.UI;
using System;
using System.Collections.Generic;
using System.IO;

namespace MapDbGen;

internal static class Program {

    // Same row format and filter as AC2RE.PacketTool's "GenerateMapDb": every CreateObject message that has a
    // position and a static id (world-placed things: NPCs, portals, lifestones, buildings' interactive parts...).
    private static int Main(string[] args) {
        if (args.Length < 2) {
            Console.WriteLine("usage: MapDbGen <folder with pcaps> <output csv> [all]");
            return 1;
        }

        string folder = args[0];
        string output = args[1];
        if (args.Length > 2 && args[2] == "events") {
            return DumpEvents(folder, output);
        }
        if (args.Length > 2 && args[2] == "track") {
            return Track(folder, output, args.Length > 3 ? args[3] : "");
        }
        if (args.Length > 2 && args[2] == "exam") {
            return DumpExams(folder, output);
        }
        if (args.Length > 4 && args[2] == "str") {
            // MOD2: what does a text-table entry say? (table id and string id in hex)
            using StreamWriter sw = new(output);
            for (int i = 3; i + 1 < args.Length; i += 2) {
                StringInfo si = new(new DataId(Convert.ToUInt32(args[i], 16)), new StringId(Convert.ToUInt32(args[i + 1], 16)));
                sw.WriteLine($"{args[i]}:{args[i + 1]}{resolveTable(si)}");
            }
            return 0;
        }
        if (args.Length > 3 && args[2] == "visualdesc") {
            // MOD2: print visual descriptions from the portal data by id (hex, comma separated)
            using DatReader portal = new(@"c:\AC2IF\AC2RE-server\data\portal.dat_server");
            PackageTypes.loadPackageTypes(portal);
            using StreamWriter vw = new(output);
            foreach (string hex in args[3].Split(',')) {
                DataId did = new(Convert.ToUInt32(hex, 16));
                vw.WriteLine($"=========== 0x{did.id:X8}");
                try {
                    using AC2Reader r = portal.getFileReader(did);
                    VisualDesc vd = new(r);
                    vw.WriteLine(Util.objectToString(vd));
                } catch (Exception e) {
                    vw.WriteLine($"failed: {e.GetType().Name}: {e.Message}");
                }
            }
            return 0;
        }
        if (args.Length > 2 && args[2] == "terrainfit") {
            // MOD2: is ground height (z) a simple function of the 17x17 height grid in the cell data? Fit it on capture positions.
            using DatReader cell = new(@"c:\AC2IF\AC2RE-server\data\cell_1.dat_server");
            Dictionary<ushort, List<byte>> grids = new();
            List<(double h, double z, ushort lb, double x, double y)> samples = new();
            List<(double a, double b, double bil, double z)> variants = new();
            List<(int b, double z)> flat = new();
            PacketUtil.processAllPcaps(folder, (pcapFileName, blobs) => {
                foreach (NetBlobRecord rec in blobs.records) {
                    PositionOffset? po = rec.message switch {
                        PositionMsg a => a.posPack.offset,
                        PositionCellMsg b => b.posPack.offset,
                        CPositionMsg c => c.posPack.offset,
                        _ => null,
                    };
                    if (po == null || po.Value.cell.indoorCellId != 0 || po.Value.cell.outdoorCellId == 0) {
                        continue;
                    }
                    ushort lb = po.Value.cell.landblockId.id;
                    if (!grids.TryGetValue(lb, out List<byte>? g)) {
                        DataId did = new(new CellId(po.Value.cell.landblockX, po.Value.cell.landblockY, 0xFF, 0xFF).id);
                        g = null;
                        if (cell.contains(did)) {
                            using AC2Reader r = cell.getFileReader(did);
                            g = new CLandBlockData(r).heights;
                        }
                        grids[lb] = g!;
                    }
                    if (g == null || g.Count < 289) {
                        continue;
                    }
                    double fx = po.Value.offset.X / 10.0, fy = po.Value.offset.Y / 10.0;
                    if (fx < 0 || fy < 0 || fx >= 16 || fy >= 16) {
                        continue;
                    }
                    int ix = (int)fx, iy = (int)fy;
                    double tx = fx - ix, ty = fy - iy;
                    double H(int i, int j) => g[i * 17 + j];
                    double h = H(ix, iy) * (1 - tx) * (1 - ty) + H(ix + 1, iy) * tx * (1 - ty) + H(ix, iy + 1) * (1 - tx) * ty + H(ix + 1, iy + 1) * tx * ty;
                    // Two-triangle surfaces: diagonal SW-NE (vertices 00, 11) or SE-NW (vertices 10, 01)
                    double triA = tx >= ty ? H(ix, iy) + (H(ix + 1, iy) - H(ix, iy)) * tx + (H(ix + 1, iy + 1) - H(ix + 1, iy)) * ty
                                           : H(ix, iy) + (H(ix + 1, iy + 1) - H(ix, iy + 1)) * tx + (H(ix, iy + 1) - H(ix, iy)) * ty;
                    double triB = tx + ty <= 1 ? H(ix, iy) + (H(ix + 1, iy) - H(ix, iy)) * tx + (H(ix, iy + 1) - H(ix, iy)) * ty
                                               : H(ix + 1, iy + 1) + (H(ix, iy + 1) - H(ix + 1, iy + 1)) * (1 - tx) + (H(ix + 1, iy) - H(ix + 1, iy + 1)) * (1 - ty);
                    variants.Add((triA, triB, h, po.Value.offset.Z));
                    if (H(ix, iy) == H(ix + 1, iy) && H(ix, iy) == H(ix, iy + 1) && H(ix, iy) == H(ix + 1, iy + 1)) {
                        flat.Add(((int)H(ix, iy), po.Value.offset.Z));
                    }
                    samples.Add((h, po.Value.offset.Z, lb, po.Value.offset.X, po.Value.offset.Y));
                }
            });
            using StreamWriter tw = new(output);
            tw.WriteLine($"samples={samples.Count} landblocks={samples.Select(s => s.lb).Distinct().Count()}");
            foreach ((string name, Func<(double a, double b, double bil, double z), double> pick) in new (string, Func<(double a, double b, double bil, double z), double>)[] { ("bilinear", v => v.bil), ("triangles SW-NE", v => v.a), ("triangles SE-NW", v => v.b) }) {
                double mh2 = variants.Average(pick), mz2 = variants.Average(v => v.z);
                double a2 = variants.Sum(v => (pick(v) - mh2) * (v.z - mz2)) / variants.Sum(v => (pick(v) - mh2) * (pick(v) - mh2)), b2 = mz2 - a2 * mh2;
                double[] errs = variants.Select(v => Math.Abs(v.z - (a2 * pick(v) + b2))).OrderBy(e => e).ToArray();
                tw.WriteLine($"{name,-18} z = {a2:F4} * h + {b2:F3}   rms={Math.Sqrt(errs.Average(e => e * e)):F3}  median abs err={errs[errs.Length / 2]:F3}  90th pct={errs[errs.Length * 9 / 10]:F3}");
            }
            tw.WriteLine("flat cells (all four corners equal): byte -> median z (count, min..max)");
            foreach (var grp in flat.GroupBy(f => f.b).OrderBy(g => g.Key)) {
                double[] zs = grp.Select(f => f.z).OrderBy(z => z).ToArray();
                tw.WriteLine($"  byte {grp.Key,3} -> z {zs[zs.Length / 2]:F2}  (n={zs.Length}, {zs[0]:F2}..{zs[^1]:F2})  z-byte={zs[zs.Length / 2] - grp.Key:F2}");
            }
            if (samples.Count > 10) {
                double mh = samples.Average(s => s.h), mz = samples.Average(s => s.z);
                double sxy = samples.Sum(s => (s.h - mh) * (s.z - mz)), sxx = samples.Sum(s => (s.h - mh) * (s.h - mh));
                double a = sxy / sxx, b = mz - a * mh;
                double rms = Math.Sqrt(samples.Average(s => Math.Pow(s.z - (a * s.h + b), 2)));
                tw.WriteLine($"fit: z = {a:F5} * h + {b:F4}   rms error = {rms:F3} m   (z range {samples.Min(s => s.z):F1}..{samples.Max(s => s.z):F1}, h range {samples.Min(s => s.h):F1}..{samples.Max(s => s.h):F1})");
                foreach (var s in samples.Where((s, i) => i % Math.Max(1, samples.Count / 25) == 0)) {
                    tw.WriteLine($"  lb={s.lb:X4} ({s.x:F1},{s.y:F1}) h={s.h:F2} z={s.z:F2} pred={(a * s.h + b):F2}");
                }
            }
            return 0;
        }
        if (args.Length > 2 && args[2] == "encprobe") {
            // MOD2: look at the raw encounter description (the data that decides what lives where) and block maps
            using DatReader portal = new(@"c:\AC2IF\AC2RE-server\data\portal.dat_server");
            using StreamWriter pw = new(output);
            foreach (DataId did in portal.dids.OrderBy(d => d.id)) {
                if ((did.id >> 24) != 0x14 && (did.id >> 24) != 0x19) {
                    continue;
                }
                byte[] bytes = portal.readFileBytes(did);
                pw.WriteLine($"DID 0x{did.id:X8} size={bytes.Length}");
                if ((did.id >> 24) == 0x14) {
                    for (int off = 0; off < Math.Min(bytes.Length, 1400); off += 32) {
                        int n = Math.Min(32, bytes.Length - off);
                        string hex = BitConverter.ToString(bytes, off, n).Replace("-", " ");
                        string asc = new string(bytes.Skip(off).Take(n).Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
                        pw.WriteLine($"  {off:X4}: {hex,-96} {asc}");
                    }
                } else {
                    uint h = BitConverter.ToUInt32(bytes, 4), wd = BitConverter.ToUInt32(bytes, 8);
                    pw.WriteLine($"  blockmap did=0x{BitConverter.ToUInt32(bytes, 0):X8} height={h} width={wd}");
                    // sample the values for the landblocks seen in the capture (0x75B9 area)
                    foreach ((int bx, int by) in new[] { (0x75, 0xB9), (0x75, 0xBA), (0x77, 0xBA), (0x7B, 0xB9), (0x74, 0xBA), (0x76, 0xB8), (0x10, 0x10) }) {
                        foreach (int idx in new[] { by * (int)wd + bx, bx * (int)h + by }) {
                            if (12 + idx < bytes.Length) {
                                pw.WriteLine($"  map[{bx:X2},{by:X2}] idx {idx} => {bytes[12 + idx]}");
                            }
                        }
                    }
                }
            }
            return 0;
        }
        if (args.Length > 2 && args[2] == "lbinfo") {
            // MOD2: what does the cell data say is placed in each landblock? (entities of the landblock-info records)
            using DatReader portal = new(@"c:\AC2IF\AC2RE-server\data\portal.dat_server");
            PackageTypes.loadPackageTypes(portal);
            using DatReader cell = new(@"c:\AC2IF\AC2RE-server\data\cell_1.dat_server");
            using StreamWriter lw = new(output);
            Dictionary<string, int> byType = new();
            int blocks = 0, withInfo = 0, withEntities = 0, failed = 0, entityTotal = 0;
            for (int y = 0; y < 256; y++) {
                for (int x = 0; x < 256; x++) {
                    DataId blockDid = new(new CellId((byte)x, (byte)y, 0xFF, 0xFF).id);
                    if (!cell.contains(blockDid)) {
                        continue;
                    }
                    blocks++;
                    try {
                        using AC2Reader r = cell.getFileReader(blockDid);
                        CLandBlockData lb = new(r);
                        if (lb.landBlockInfoDid.id == 0 || !cell.contains(lb.landBlockInfoDid)) {
                            continue;
                        }
                        withInfo++;
                        using AC2Reader ir = cell.getFileReader(lb.landBlockInfoDid);
                        CLandBlockInfo info = new(ir);
                        int n = info.entities?.entities?.Count ?? 0;
                        if (n > 0) {
                            withEntities++;
                            entityTotal += n;
                            foreach (EntityDesc e in info.entities!.entities) {
                                string k = $"{e.type}/{e.packageType}";
                                byType[k] = byType.GetValueOrDefault(k) + 1;
                            }
                            if (withEntities <= 12) {
                                lw.WriteLine($"landblock <{x:X2} {y:X2}> info=0x{lb.landBlockInfoDid.id:X8} entities={n}: " + string.Join("; ", info.entities.entities.Take(8).Select(e => $"{e.type} pkg={e.packageType} did=0x{e.dataId.id:X8} runtime={e.runtimeId} pos={e.offset.Translation}")));
                            }
                        }
                    } catch (Exception e) {
                        failed++;
                        if (failed <= 3) {
                            lw.WriteLine($"landblock <{x:X2} {y:X2}> failed: {e.GetType().Name}: {e.Message}");
                        }
                    }
                }
            }
            lw.WriteLine($"landblocks={blocks} withInfo={withInfo} withEntities={withEntities} failed={failed} entityTotal={entityTotal}");
            lw.WriteLine("entities by TYPE: " + string.Join(", ", byType.GroupBy(kv => kv.Key.Split('/')[0]).Select(g => $"{g.Key}={g.Sum(kv => kv.Value)}")));
            foreach ((string k, int v) in byType.OrderByDescending(kv => kv.Value).Take(40)) {
                lw.WriteLine($"{v,7}  {k}");
            }
            return 0;
        }
        if (args.Length > 4 && args[2] == "window") {
            // MOD2: print every message between two capture timestamps in full
            double t0 = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            double t1 = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
            using StreamWriter ww = new(output);
            PacketUtil.processAllPcaps(folder, (pcapFileName, blobs) => {
                foreach (NetBlobRecord r in blobs.records) {
                    if (r.message == null || r.startTimestamp < t0 || r.startTimestamp > t1) {
                        continue;
                    }
                    string name = r.message.GetType().Name;
                    if (name.StartsWith("PositionMsg") || name.StartsWith("CPosition") || name.StartsWith("LookAt") || name.StartsWith("CLookAt")) {
                        continue;
                    }
                    if (r.message is InterpCEventPrivateMsg pc) name += " " + pc.netEvent.GetType().Name;
                    if (r.message is InterpSEventMsg sc) name += " " + sc.netEvent.GetType().Name;
                    if (r.message is InterpCEventVisualMsg vc) name += " " + vc.netEvent.GetType().Name;
                    ww.WriteLine($"---- t={r.startTimestamp:F2} {(r.isClientToServer ? "C->S" : "S->C")} {name}");
                    ww.WriteLine(Util.objectToString(r.message));
                }
            });
            return 0;
        }
        if (args.Length > 2 && args[2] == "equip") {
            // MOD2: everything the real server did around each equip / unequip / level-up / examine-by-force event
            using StreamWriter ew = new(output);
            int shown = 0;
            PacketUtil.processAllPcaps(folder, (pcapFileName, blobs) => {
                List<NetBlobRecord> recs = blobs.records.Where(r => r.message != null).ToList();
                HashSet<int> printed = new();
                for (int i = 0; i < recs.Count; i++) {
                    bool hit = (recs[i].message is InterpCEventPrivateMsg p && (p.netEvent is EquipItemDoneCEvt || p.netEvent is UnequipItemDoneCEvt))
                        || (recs[i].message is InterpSEventMsg s && (s.netEvent.GetType().Name.Contains("Equip")));
                    if (!hit || shown > 14) {
                        continue;
                    }
                    shown++;
                    ew.WriteLine($"################ event #{shown} at t={recs[i].startTimestamp:F2}");
                    for (int j = Math.Max(0, i - 3); j < Math.Min(recs.Count, i + 14); j++) {
                        if (!printed.Add(j)) {
                            continue;
                        }
                        NetBlobRecord r = recs[j];
                        string name = r.message!.GetType().Name;
                        if (r.message is InterpCEventPrivateMsg pc) name += " " + pc.netEvent.GetType().Name;
                        if (r.message is InterpSEventMsg sc) name += " " + sc.netEvent.GetType().Name;
                        if (r.message is InterpCEventVisualMsg vc) name += " " + vc.netEvent.GetType().Name;
                        bool skip = name.StartsWith("PositionMsg") || name.StartsWith("CPositionMsg") || name.StartsWith("LookAt") || name.StartsWith("CLookAt") || name.StartsWith("UpdateSelection") || name.Contains("UpdateAttackState") || name.Contains("Examination");
                        if (skip) {
                            continue;
                        }
                        ew.WriteLine($"---- t={r.startTimestamp:F2} {(r.isClientToServer ? "C->S" : "S->C")} {name}");
                        if (name.Contains("Equip") || name.StartsWith("ParentMsg") || name.StartsWith("DeParent") || name.StartsWith("UpdateVisualDesc") || name.StartsWith("QualUpdate") || name.StartsWith("ContainMsg") || name.Contains("Mode") || name.Contains("MoveItem") || name.Contains("Behavior")) {
                            ew.WriteLine(Util.objectToString(r.message));
                        }
                    }
                }
            });
            return 0;
        }
        if (args.Length > 2 && args[2] == "quals") {
            // MOD2: every private quality update the real server sent (which stats change, e.g. on level-up) plus related events
            Dictionary<string, int> qc = new();
            using StreamWriter qw = new(output);
            PacketUtil.processAllPcaps(folder, (pcapFileName, blobs) => {
                foreach (NetBlobRecord rec in blobs.records) {
                    object? m = rec.message;
                    if (m == null) {
                        continue;
                    }
                    string? line = m switch {
                        QualUpdateIntPrivateMsg a => $"Int {a.type} = {a.value}",
                        QualUpdateLongIntPrivateMsg b => $"Long {b.type} = {b.value}",
                        QualUpdateBoolPrivateMsg c => $"Bool {c.type} = {c.value}",
                        QualUpdateFloatPrivateMsg d => $"Float {d.type} = {d.value}",
                        _ => null,
                    };
                    if (line == null && m is InterpCEventPrivateMsg pe && !(pe.netEvent is UpdateExaminationProfileCEvt || pe.netEvent is UpdateSelectionInfoCEvt || pe.netEvent is UpdateAttackStateCEvt)) {
                        line = "Event " + pe.netEvent.GetType().Name;
                    }
                    if (line == null && m is DoFxMsg fx && fx.senderIdWithStamp.id.type == InstanceId.IdType.Player) {
                        line = "PlayerFx " + fx.fxId;
                    }
                    if (line == null && m is DisplayStringInfoMsg ds) {
                        line = "Text " + fmtStr(ds.text) + " type=" + ds.type;
                    }
                    if (line == null) {
                        continue;
                    }
                    string kind = line.Split(' ')[0] == "Int" || line.StartsWith("Long") || line.StartsWith("Bool") || line.StartsWith("Float") ? string.Join(" ", line.Split(' ').Take(2)) : line.Length > 60 ? line[..60] : line;
                    qc[kind] = qc.GetValueOrDefault(kind) + 1;
                    if (line.StartsWith("Int Level") || line.StartsWith("Long TotalXP") || line.StartsWith("Long AvailableXP") || line.StartsWith("Int Health_CachedMax") || line.StartsWith("Int Vigor_CachedMax") || line.StartsWith("Text") || line.StartsWith("PlayerFx") || line.StartsWith("Event")) {
                        qw.WriteLine($"t={rec.startTimestamp,9:F1}  {line}");
                    }
                }
            });
            qw.WriteLine("==== COUNTS");
            foreach ((string k, int v) in qc.OrderByDescending(x => x.Value)) {
                qw.WriteLine($"{v,6}  {k}");
            }
            return 0;
        }
        if (args.Length > 2 && args[2] == "levels") {
            // MOD2: the character level table from the game's own data (portal.dat entry 0x70000380)
            using DatReader portal = new(@"c:\AC2IF\AC2RE-server\data\portal.dat_server");
            PackageTypes.loadPackageTypes(portal);
            using AC2Reader data = portal.getFileReader(new DataId(0x70000380));
            LevelTable table = (LevelTable)new WState(data).package;
            using StreamWriter lw = new(output);
            lw.WriteLine($"maxLevel={table.maxLevel} entries={table.map.Count}");
            lw.WriteLine("index,xp,deathXp,health,vigor,skillCredits,heroSkillCredits,heroCap,maxArmor,maxShield");
            for (int i = 0; i < table.map.Count; i++) {
                LevelData d = table.map[i];
                lw.WriteLine($"{i},{d.xp},{d.deathXp},{d.health},{d.vigor},{d.skillCredits},{d.heroSkillCredits},{d.heroSkillCreditCap},{d.maxArmorValue},{d.maxShieldValue}");
            }
            return 0;
        }
        bool all = args.Length > 2 && args[2] == "all";

        HashSet<InstanceId> processedIds = new();
        Dictionary<string, int> perPackage = new();
        Dictionary<string, int> perType = new();
        int messages = 0, createObject = 0, written = 0;

        using (StreamWriter csv = new(File.Open(output, FileMode.Create, FileAccess.Write, FileShare.Read))) {
            csv.WriteLine("packageType,id,entityDid,landblockId,cellId,posX,posY,posZ,rotX,rotY,rotZ,rotW,scale,nameStringId,nameTableDid");
            PacketUtil.processAllPcaps(folder, (pcapFileName, netBlobCollection) => {
                Console.WriteLine($"read {Path.GetFileName(pcapFileName)}: {netBlobCollection.records.Count} messages");
                foreach (NetBlobRecord rec in netBlobCollection.records) {
                    messages++;
                    if (rec.message is not CreateObjectMsg msg) {
                        continue;
                    }
                    createObject++;
                    string t = msg.id.type.ToString();
                    perType[t] = perType.GetValueOrDefault(t) + 1;
                    if (msg.physicsDesc.pos == null || (!all && msg.id.type != InstanceId.IdType.Static) || !processedIds.Add(msg.id)) {
                        continue;
                    }
                    Position pos = msg.physicsDesc.pos;
                    csv.WriteLine($"{msg.weenieDesc.packageType},{msg.id.id},{msg.weenieDesc.entityDid.id},{pos.cell.landblockId.id},{pos.cell.localCellId.id},{pos.frame.pos.X},{pos.frame.pos.Y},{pos.frame.pos.Z},{pos.frame.rot.X},{pos.frame.rot.Y},{pos.frame.rot.Z},{pos.frame.rot.W},{msg.weenieDesc.scale},{msg.weenieDesc.name?.stringId.ToString() ?? "NULL"},{msg.weenieDesc.name?.tableDid.id.ToString() ?? "NULL"}");
                    written++;
                    string p = msg.weenieDesc.packageType.ToString();
                    perPackage[p] = perPackage.GetValueOrDefault(p) + 1;
                }
            });
        }

        Console.WriteLine($"messages parsed: {messages}, CreateObject messages: {createObject}, rows written: {written}");
        Console.WriteLine("CreateObject by id type: " + string.Join(", ", perType));
        Console.WriteLine("rows by package type:    " + string.Join(", ", perPackage));
        Console.WriteLine($"output: {Path.GetFullPath(output)}");
        return 0;
    }

    // MOD2: every "examine / identify" answer the real server sent, with what was examined and each display node
    private static DatReader? localDat;
    private static readonly Dictionary<uint, StringTable> stringTables = new();

    // The text a table string stands for (with its %variable% placeholders), read from the client's string tables
    private static string resolveTable(StringInfo s) {
        try {
            localDat ??= new DatReader(@"c:\AC2IF\AC2RE-server\data\local_English.dat_server");
            if (!stringTables.TryGetValue(s.tableDid.id, out StringTable? table)) {
                using AC2Reader r = localDat.getFileReader(s.tableDid);
                table = new StringTable(r);
                stringTables[s.tableDid.id] = table;
            }
            StringTableString entry = table.strings[s.stringId];
            string names = entry.varNames != null && entry.varNames.Count > 0 ? " {" + string.Join(",", entry.varNames) + "}" : (entry.variables != null && entry.variables.Count > 0 ? " {" + string.Join(",", entry.variables) + "}" : "");
            return $" => \"{entry.strings.FirstOrDefault()?.Replace("\n", "\\n")}\"{names}";
        } catch (Exception e) {
            return $" => <unresolved: {e.GetType().Name}>";
        }
    }

    private static string fmtStr(StringInfo? s) {
        if (s == null) {
            return "null";
        }
        string vars = "";
        if (s.variables != null && s.variables.Count > 0) {
            vars = " vars{" + string.Join("; ", s.variables.Select(kv => $"{kv.Key}={fmtData(kv.Value)}")) + "}";
        }
        string text = s.literalValue != null ? $"\"{s.literalValue}\"" : $"T(0x{s.tableDid.id:X8}:0x{s.stringId.id:X8}){(s.tableDid.id != 0 && s.stringId.id != 0 ? resolveTable(s) : "")}";
        return text + vars;
    }

    private static string fmtData(StringInfoData d) => d.type switch {
        StringInfoData.DataType.Int or StringInfoData.DataType.FormattedInt => $"int {d.valInt}",
        StringInfoData.DataType.UInt or StringInfoData.DataType.FormattedUInt => $"uint {d.valUInt}",
        StringInfoData.DataType.Float or StringInfoData.DataType.FormattedFloat => $"float {d.valDouble}",
        StringInfoData.DataType.LInt or StringInfoData.DataType.FormattedLInt => $"long {d.valLong}",
        StringInfoData.DataType.ULInt or StringInfoData.DataType.FormattedULInt => $"ulong {d.valULong}",
        StringInfoData.DataType.StringInfo => $"str {fmtStr(d.valString)}",
        _ => d.type.ToString(),
    };

    private static string fmtNode(ExaminationDataNode n) => n.type switch {
        ExaminationDataNode.DataType.Int => $"[{n.order}] Int app={n.appearanceId} {n.valInt}",
        ExaminationDataNode.DataType.EffectID => $"[{n.order}] EffectID app={n.appearanceId} {n.valInt}",
        ExaminationDataNode.DataType.Float => $"[{n.order}] Float app={n.appearanceId} {n.valFloat}",
        ExaminationDataNode.DataType.Bool => $"[{n.order}] Bool app={n.appearanceId} {n.valBool}",
        ExaminationDataNode.DataType.String => $"[{n.order}] Str app={n.appearanceId} {fmtStr(n.valString)}",
        ExaminationDataNode.DataType.Image => $"[{n.order}] Image 0x{n.valDataID.id:X8}",
        ExaminationDataNode.DataType.Break => $"[{n.order}] Break",
        ExaminationDataNode.DataType.Tab => $"[{n.order}] Tab {n.valInt}",
        ExaminationDataNode.DataType.Countdown => $"[{n.order}] Countdown app={n.appearanceId} {n.valTime}",
        ExaminationDataNode.DataType.LongInt => $"[{n.order}] LongInt app={n.appearanceId} {n.valLongInt}",
        ExaminationDataNode.DataType.IconDesc => $"[{n.order}] IconDesc layers={n.valIconDesc?.layers?.Count}",
        _ => $"[{n.order}] {n.type}",
    };

    private static int DumpExams(string folder, string output) {
        Dictionary<ulong, string> known = new();
        Dictionary<string, int> byType = new();
        int responses = 0;
        using (StreamWriter w = new(File.Open(output, FileMode.Create, FileAccess.Write, FileShare.Read))) {
            PacketUtil.processAllPcaps(folder, (pcapFileName, blobs) => {
                foreach (NetBlobRecord rec in blobs.records) {
                    if (rec.message == null) {
                        continue;
                    }
                    if (rec.message is CreateObjectMsg co && co.weenieDesc != null) {
                        known[co.id.id] = $"pkg={co.weenieDesc.packageType} entity=0x{co.weenieDesc.entityDid.id:X8} name={fmtStr(co.weenieDesc.name)} container={co.weenieDesc.containerId.type}";
                    }
                    if (rec.message is InterpSEventMsg sev && sev.netEvent is QueryExaminationProfileSEvt q) {
                        ExaminationRequest r = q.request;
                        w.WriteLine($"-- t={rec.startTimestamp:F1} QUERY type={r.type} admin={r.admin} enum=0x{r.dataEnum:X} did=0x{r.dataDid.id:X8} id={r.dataId} unk1={r.unk1} unk2={r.unk2} unk3={r.unk3}");
                    }
                    if (rec.message is InterpCEventPrivateMsg pe && pe.netEvent is UpdateExaminationProfileCEvt u) {
                        responses++;
                        ExaminationRequest r = u.profile.request;
                        byType[r.type.ToString()] = byType.GetValueOrDefault(r.type.ToString()) + 1;
                        string target = r.dataId.id != 0 && known.TryGetValue(r.dataId.id, out string? k) ? k : "";
                        w.WriteLine($"==== t={rec.startTimestamp:F1} RESPONSE type={r.type} admin={r.admin} enum=0x{r.dataEnum:X} did=0x{r.dataDid.id:X8} id={r.dataId} unk3={r.unk3} unk1={u.profile.unk1} nodes={u.profile.nodes?.Count}  target: {target}");
                        if (u.profile.nodes != null) {
                            foreach (ExaminationDataNode n in u.profile.nodes) {
                                w.WriteLine("      " + fmtNode(n));
                            }
                        }
                    }
                }
            });
            w.WriteLine("==== SUMMARY: " + responses + " responses; by request type: " + string.Join(", ", byType.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        Console.WriteLine($"examine dump written to {Path.GetFullPath(output)}");
        return 0;
    }

    // MOD2: follow ONE dynamic object (a monster) through the capture: where it stood, how it moved, which behaviors / attacks
    // it did and where the player was at the time. With no id given it lists the dynamic objects that moved the most.
    private static int Track(string folder, string output, string hexId) {
        ulong wanted = hexId.Length > 0 ? Convert.ToUInt64(hexId, 16) : 0;
        Dictionary<ulong, (int n, double first, double last)> posCounts = new();
        string playerPos = "?";
        PositionOffset? playerOffset = null;
        using (StreamWriter w = new(File.Open(output, FileMode.Create, FileAccess.Write, FileShare.Read))) {
            PacketUtil.processAllPcaps(folder, (pcapFileName, blobs) => {
                foreach (NetBlobRecord rec in blobs.records) {
                    if (rec.message == null) {
                        continue;
                    }
                    if (rec.message is CPositionMsg cp) {
                        playerOffset = cp.posPack.offset;
                        playerPos = $"{cp.posPack.offset.cell} {cp.posPack.offset.offset} motion={cp.posPack.doMotion} head={cp.posPack.heading}";
                        continue;
                    }
                    if (wanted != 0 && rec.message is CreateObjectMsg created && created.id.id == wanted) {
                        w.WriteLine($"t={rec.startTimestamp,9:F2}  CREATE-OBJECT of the tracked creature:");
                        w.WriteLine(Util.objectToString(rec.message));
                        continue;
                    }
                    InstanceIdWithStamp? sender = rec.message.GetType().GetField("senderIdWithStamp")?.GetValue(rec.message) as InstanceIdWithStamp;
                    if (sender == null || sender.id.type != InstanceId.IdType.Dynamic) {
                        if (wanted != 0 && rec.message is InterpSEventMsg sev && sev.netEvent is DoAttackSEvt atk && atk.targetId.id == wanted) {
                            w.WriteLine($"t={rec.startTimestamp,9:F2}  PLAYER ATTACKS monster: skill={atk.skillId} special={atk.specialAttackId}   player at {playerPos}");
                        }
                        continue;
                    }
                    PositionPack? pp = (rec.message as PositionMsg)?.posPack ?? (rec.message as PositionCellMsg)?.posPack;
                    if (pp != null) {
                        (int n, double first, double last) c = posCounts.GetValueOrDefault(sender.id.id);
                        posCounts[sender.id.id] = (c.n + 1, c.n == 0 ? rec.startTimestamp : c.first, rec.startTimestamp);
                    }
                    if (wanted == 0 || sender.id.id != wanted) {
                        continue;
                    }
                    string dist = "";
                    if (pp != null && playerOffset != null && playerOffset.Value.cell.landblockId.id == pp.offset.cell.landblockId.id) {
                        dist = $" dist-to-player={System.Numerics.Vector3.Distance(playerOffset.Value.offset, pp.offset.offset):F1}";
                    }
                    string what = rec.message switch {
                        PositionMsg or PositionCellMsg => $"POS {rec.message.GetType().Name} {pp!.offset.cell} {pp.offset.offset} motion={pp.doMotion} head={pp.heading} stamp={pp.posStamp} contact={pp.packFlags}{dist}",
                        DoBehaviorMsg b => $"DO-BEHAVIOR {b.behaviorParams.behaviorId} timeScale={b.behaviorParams.timeScale} mode={b.behaviorParams.modeId} target={b.behaviorParams.targetId} flags={b.behaviorParams.packFlags}",
                        StopBehaviorMsg sb => $"STOP-BEHAVIOR {sb.behaviorId}",
                        DoModeMsg m => $"DO-MODE {m.modeId}",
                        DoStoryMsg s => $"DO-STORY behavior={s.story.attackerBehavior?.behaviorId} skill={s.story.skillId} timeScale={s.story.attackerBehavior?.timeScale} hooks=[{string.Join("; ", s.story.hooks?.Select(h => $"{h.type} target={h.targetId} result={h.attackResult} hpChange={h.targetHealthChange}") ?? new string[0])}]",
                        DoFxMsg f => $"DO-FX {f.fxId} scalar={f.scalar}",
                        StopFxMsg sf => $"STOP-FX {sf.fxId}",
                        MoveToMsg mt => $"MOVE-TO target={mt.movementParams.targetId} flags={mt.movementParams.packFlags} vel={mt.movementParams.desiredVel} dist={mt.movementParams.desiredDist}",
                        LookAtMsg la => $"LOOK-AT {la.targetId}",
                        LookAtDirMsg ld => $"LOOK-AT-DIR x={ld.x} z={ld.z}",
                        _ => rec.message.GetType().Name,
                    };
                    w.WriteLine($"t={rec.startTimestamp,9:F2}  MON  {what}   (player at {playerPos})");
                }
            });
            w.WriteLine("==== dynamic objects by number of position messages (id, count, first t, last t)");
            foreach (var kv in posCounts.OrderByDescending(x => x.Value.n).Take(25)) {
                w.WriteLine($"0x{kv.Key:X16}  {kv.Value.n,5}  {kv.Value.first,9:F1}  {kv.Value.last,9:F1}");
            }
        }
        Console.WriteLine($"track written to {Path.GetFullPath(output)}");
        return 0;
    }

    // MOD2 diagnostics: which message types does the capture contain, and what do teleports / portal use look like?
    private static int DumpEvents(string folder, string output) {
        Dictionary<string, int> counts = new();
        Dictionary<string, int> dumped = new();
        HashSet<InstanceId> corpseIds = new();
        List<string> timeline = new();
        string lastCell = "";
        using (StreamWriter w = new(File.Open(output, FileMode.Create, FileAccess.Write, FileShare.Read))) {
            PacketUtil.processAllPcaps(folder, (pcapFileName, blobs) => {
                foreach (NetBlobRecord rec in blobs.records) {
                    if (rec.message == null) {
                        continue;
                    }
                    // MOD2: timeline of uses, portal space and where the player was
                    if (rec.message is InterpSEventMsg useMsg && useMsg.netEvent is TryToUseItemSEvt use) {
                        timeline.Add($"t={rec.startTimestamp,8:F1}  USE    item {use.usageDesc.itemId}");
                    } else if (rec.message is InterpCEventPrivateMsg pe && pe.netEvent is EnterPortalSpaceCEvt) {
                        timeline.Add($"t={rec.startTimestamp,8:F1}  ENTER-PORTAL-SPACE");
                    } else if (rec.message is InterpCEventPrivateMsg px && px.netEvent is ExitPortalSpaceCEvt) {
                        timeline.Add($"t={rec.startTimestamp,8:F1}  EXIT-PORTAL-SPACE");
                    } else if (rec.message is PositionCellMsg pc && pc.senderIdWithStamp.id.type == InstanceId.IdType.Player) {
                        string cell = pc.posPack.offset.cell.ToString() ?? "";
                        if (cell != lastCell) {
                            timeline.Add($"t={rec.startTimestamp,8:F1}  POSITION cell {cell} offset {pc.posPack.offset.offset} teleportStamp {pc.posPack.teleportStamp}");
                            lastCell = cell;
                        }
                    }
                    string name = rec.message.GetType().Name;
                    string detail = name;
                    if (rec.message is InterpSEventMsg se) {
                        detail = name + " " + se.netEvent.GetType().Name;
                    } else if (rec.message is InterpCEventPrivateMsg ce) {
                        detail = name + " " + ce.netEvent.GetType().Name;
                    } else if (rec.message is InterpCEventVisualMsg cv) {
                        detail = name + " " + cv.netEvent.GetType().Name;
                    }
                    counts[detail] = counts.GetValueOrDefault(detail) + 1;
                    // MOD2: what do messages FROM monsters (Dynamic senders) look like?
                    InstanceIdWithStamp? sender = rec.message.GetType().GetField("senderIdWithStamp")?.GetValue(rec.message) as InstanceIdWithStamp;
                    if (sender != null && sender.id.type == InstanceId.IdType.Dynamic && !rec.isClientToServer) {
                        string mk = "FROM-DYNAMIC " + name + (rec.message is InterpCEventVisualMsg cvv ? " " + cvv.netEvent.GetType().Name : "");
                        counts[mk] = counts.GetValueOrDefault(mk) + 1;
                        if (dumped.GetValueOrDefault(mk) < 3) {
                            dumped[mk] = dumped.GetValueOrDefault(mk) + 1;
                            w.WriteLine($"==== t={rec.startTimestamp:F2} {mk}");
                            w.WriteLine(Util.objectToString(rec.message));
                        }
                    }
                    if (rec.message is DoStoryMsg ds && ds.senderIdWithStamp.id.type == InstanceId.IdType.Dynamic && dumped.GetValueOrDefault("MON-STORY") < 6) {
                        dumped["MON-STORY"] = dumped.GetValueOrDefault("MON-STORY") + 1;
                        w.WriteLine($"==== t={rec.startTimestamp:F2} MON-STORY (a dynamic object telling a physics story)");
                        w.WriteLine(Util.objectToString(rec.message));
                    }
                    if (rec.message is CreateObjectMsg cor && cor.weenieDesc != null && cor.weenieDesc.packageType == PackageType.Corpse) {
                        corpseIds.Add(cor.id);
                        w.WriteLine($"==== CORPSE-ID {cor.id} name={cor.weenieDesc.name?.stringId} entityDid={cor.weenieDesc.entityDid.id}");
                    }
                    if (rec.message is CreateObjectMsg lo && lo.weenieDesc != null && corpseIds.Contains(lo.weenieDesc.containerId)) {
                        w.WriteLine($"==== CORPSE-LOOT t={rec.startTimestamp:F2} corpse={lo.weenieDesc.containerId.id:X} item pkg={lo.weenieDesc.packageType} entityDid=0x{lo.weenieDesc.entityDid.id:X8} nameStringId=0x{lo.weenieDesc.name?.stringId:X8} nameTableDid=0x{lo.weenieDesc.name?.tableDid.id:X8} icon=0x{lo.weenieDesc.iconDid.id:X8} qty={lo.weenieDesc.quantity} value={lo.weenieDesc.value} physDid=0x{lo.visualDesc?.parentDid.id:X8}");
                    }
                    // MOD2: monster behavior and loot evidence
                    if (rec.message is CreateObjectMsg co && co.weenieDesc != null && co.weenieDesc.containerId != InstanceId.NULL) {
                        string cn = "LOOT-ITEM";
                        counts[cn] = counts.GetValueOrDefault(cn) + 1;
                        if (dumped.GetValueOrDefault(cn) < 40) {
                            dumped[cn] = dumped.GetValueOrDefault(cn) + 1;
                            w.WriteLine($"==== t={rec.startTimestamp:F2} {cn} pkg={co.weenieDesc.packageType} entityDid={co.weenieDesc.entityDid.id} container={co.weenieDesc.containerId} name={co.weenieDesc.name?.stringId} id={co.id}");
                        }
                    }
                    if (rec.message is CreateObjectMsg cc && cc.weenieDesc != null && cc.weenieDesc.packageType == PackageType.Corpse && dumped.GetValueOrDefault("CORPSE") < 6) {
                        dumped["CORPSE"] = dumped.GetValueOrDefault("CORPSE") + 1;
                        w.WriteLine($"==== t={rec.startTimestamp:F2} CORPSE");
                        w.WriteLine(Util.objectToString(rec.message));
                    }
                    bool interesting = detail.Contains("Portal") || detail.Contains("Teleport") || detail.Contains("TryToUseItem")
                        || detail.Contains("DoStory") || detail.Contains("DoBehavior") || detail.Contains("DoMode") || detail.Contains("DestroyObject")
                        || detail.Contains("DoFx") || detail.Contains("QualUpdateInt") || detail.Equals("PositionMsg") || detail.Equals("LookAtMsg")
                        || detail.Contains("Lifestone") || detail.Contains("PositionCell") || detail.Contains("Bind")
                        || detail.Contains("Selection") || detail.Contains("AttackError") || detail.Contains("DoAttack")
                        || detail.Contains("UpdateAttackState") || detail.Contains("ExaminationProfile") || detail.Contains("MoveTo")
                        || detail.Contains("DisplayStringInfo") || detail.Contains("EndSpecialAttack") || detail.Contains("LeaveWorld");
                    if (interesting && dumped.GetValueOrDefault(detail) < 6) {
                        dumped[detail] = dumped.GetValueOrDefault(detail) + 1;
                        w.WriteLine($"==== t={rec.startTimestamp:F2} {(rec.isClientToServer ? "C->S" : "S->C")} {detail}");
                        w.WriteLine(Util.objectToString(rec.message));
                    }
                }
            });
            w.WriteLine("==== TIMELINE");
            foreach (string line in timeline) {
                w.WriteLine(line);
            }
            w.WriteLine("==== COUNTS");
            foreach ((string k, int v) in counts.OrderByDescending(x => x.Value)) {
                w.WriteLine($"{v,6}  {k}");
            }
        }
        Console.WriteLine($"event dump written to {Path.GetFullPath(output)}");
        return 0;
    }
}

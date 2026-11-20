using AC2RE.Definitions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AC2RE.Server;

/// <summary>
/// MOD2: the "examine / identify" panel.
///
/// The client asks "examine this object" and the server answers with a list of display nodes (labels, numbers, line breaks, icons)
/// that the client lays out. Upstream's answer was a developer dump of every raw property ("Health_CurrentLevel 86",
/// "WeenieType 31", ...), and it answered with a made-up "12345" for anything it didn't understand. This builds what the real server
/// sent in your capture, node for node: the same text-table entries (the client turns them into words such as "Health:",
/// "Damage:", "Minimum Level:"), the same two text styles, and the same section numbers (2 = heading, 8 = stats, 32 = requirements);
/// the profile's "unk1" is the OR of the sections it contains (the capture: 2, 34, 42, 26).
///
/// What each kind of thing shows (all from the capture):
///  - NPCs, portals, stones, signs, lifestones...: name, then the description from the object's own text table.
///  - creatures: name, level, description, health and vigor (current / maximum).
///  - items: icon, name, value, description, weapon or armor stats, requirements (minimum level, race).
///  - characters: name, race, level, kingdom, health, vigor, experience (total, needed for next level, available).
/// </summary>
internal class ExaminationBuilder {

    // The two text styles the real server used
    private const uint TITLE = 3193660691; // names and values
    private const uint LABEL = 868106293; // labels and descriptions

    // Sections ("order" values; they are bit flags)
    private const uint HEADER = 2;
    private const uint STATS = 8;
    private const uint REQUIREMENTS = 32;

    // Most labels live in this client text table (ids copied from the capture)
    private static readonly DataId TEXT = new(0x25000011);

    // Variables of the text-table entries (keys copied from the capture)
    private const uint VAR_VALUE = 218522968;
    private const uint VAR_HEALTH_NOW = 925941784, VAR_HEALTH_MAX = 727748632;
    private const uint VAR_VIGOR_NOW = 3065071465, VAR_VIGOR_MAX = 2866878313;
    private const uint VAR_DAMAGE_MIN = 2602987520, VAR_DAMAGE_MAX = 2600910848;
    private const uint VAR_COUNT = 121548010;
    private const uint VAR_NAME = 2634443856, VAR_TITLE = 153527496;

    // An object's description is the string 0x6C459364 of the same text table as its name
    private const uint DESCRIPTION_STRING = 0x6C459364;

    private readonly World world;

    public ExaminationBuilder(World world) {
        this.world = world;
    }

    /// <summary>The answer to an examine request, or null when the request isn't about an object we know (the real server stayed silent too).</summary>
    public ExaminationProfile? build(ExaminationRequest request) {
        if (request.type != ExaminationRequest.RequestType.Item && request.type != ExaminationRequest.RequestType.Unk1) {
            return null;
        }
        if (!world.objectManager.tryGet(request.dataId, out WorldObject? target)) {
            return null;
        }

        List<ExaminationDataNode> nodes = new();
        if (target.player != null || target.packageType == PackageType.PlayerAvatar) {
            addCharacter(nodes, target);
        } else if (isCreature(target)) {
            addCreature(nodes, target);
        } else {
            addObject(nodes, target);
        }

        uint sections = 0;
        foreach (ExaminationDataNode node in nodes) {
            sections |= node.order;
        }

        return new ExaminationProfile {
            request = request,
            nodes = nodes,
            unk1 = sections,
        };
    }

    /// <summary>Send a fresh profile of <paramref name="target"/> to a player who is looking at it (the real server did this after every hit).</summary>
    public void push(Player player, WorldObject target) {
        ExaminationProfile? profile = build(new ExaminationRequest {
            type = isCreature(target) ? ExaminationRequest.RequestType.Unk1 : ExaminationRequest.RequestType.Item,
            dataId = target.id,
            unk3 = 1023,
        });
        if (profile != null) {
            world.playerManager.send(player, new UpdateExaminationProfileCEvt {
                profile = profile,
            });
        }
    }

    private bool isCreature(WorldObject target) {
        return world.monsterAi.isCreature(target.id) || (target.player == null && target.packageType != PackageType.PlayerAvatar && target.healthMax > 0);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Creatures
    // ---------------------------------------------------------------------------------------------------------------

    private void addCreature(List<ExaminationDataNode> nodes, WorldObject creature) {
        int level = Math.Max(creature.level, 1);
        StringInfo? description = describe(creature);

        nodes.Add(text(HEADER, TITLE, nameOf(creature)));
        nodes.Add(lineBreak(HEADER));
        nodes.Add(lineBreak(HEADER));
        nodes.Add(text(HEADER, LABEL, table(0x2EA010CC, (420918361, level)))); // "Level: " (the variable is SkillLevel)
        nodes.Add(number(HEADER, LABEL, level));
        nodes.Add(lineBreak(HEADER));
        if (description != null) {
            nodes.Add(text(HEADER, LABEL, description));
            nodes.Add(lineBreak(HEADER));
            nodes.Add(text(HEADER, LABEL, literal("")));
            nodes.Add(lineBreak(HEADER));
        }
        nodes.Add(lineBreak(HEADER));
        addVitals(nodes, creature);
    }

    private static void addVitals(List<ExaminationDataNode> nodes, WorldObject target) {
        nodes.Add(text(HEADER, LABEL, table(0x186AA4E6))); // "Health: "
        nodes.Add(text(HEADER, TITLE, table(0x2B64885C, (VAR_HEALTH_NOW, target.health), (VAR_HEALTH_MAX, target.healthMax))));
        nodes.Add(lineBreak(HEADER));
        nodes.Add(text(HEADER, LABEL, table(0x2DF35274))); // "Vigor: "
        nodes.Add(text(HEADER, TITLE, table(0xAAE50F6D, (VAR_VIGOR_NOW, target.vigor), (VAR_VIGOR_MAX, target.vigorMax))));
        nodes.Add(lineBreak(HEADER));
        nodes.Add(lineBreak(HEADER));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Characters
    // ---------------------------------------------------------------------------------------------------------------

    private void addCharacter(List<ExaminationDataNode> nodes, WorldObject character) {
        int level = Math.Max(character.level, 1);

        string name = "Unknown";
        try {
            name = character.name != null ? (character.name.literalValue ?? world.contentManager.getString(character.name)) : name;
        } catch (Exception) {
        }
        // The names of characters carry a sex tag ("Nerd [M]"), which the client's own panel shows as part of the name

        nodes.Add(text(HEADER, TITLE, tableText(0xBFB4ECCD, (VAR_TITLE, literal("")), (VAR_NAME, literal(name)))));
        nodes.Add(lineBreak(HEADER));
        nodes.Add(lineBreak(HEADER));
        StringInfo? species = speciesName(character.species);
        if (species != null) {
            nodes.Add(text(HEADER, TITLE, species));
            nodes.Add(lineBreak(HEADER));
        }
        nodes.Add(text(HEADER, LABEL, literal("")));
        nodes.Add(lineBreak(HEADER));
        nodes.Add(text(HEADER, LABEL, table(0x2EA010CC, (420918361, level)))); // "Level: "
        nodes.Add(number(HEADER, LABEL, level));
        nodes.Add(lineBreak(HEADER));
        nodes.Add(text(HEADER, LABEL, table(0x13E895B3))); // "Kingdom: "
        nodes.Add(text(HEADER, LABEL, new StringInfo(new DataId(0x2500000F), new StringId(0x126895B3)))); // "Neutral"
        nodes.Add(lineBreak(HEADER));
        nodes.Add(text(HEADER, LABEL, literal("")));
        nodes.Add(lineBreak(HEADER));
        addVitals(nodes, character);

        nodes.Add(text(STATS, LABEL, table(0xE237DBF4))); // "Experience Stats:"
        nodes.Add(lineBreak(STATS));
        nodes.Add(tab(STATS));
        nodes.Add(text(STATS, LABEL, table(0x2E2211FC))); // "Total XP: "
        nodes.Add(bigNumber(STATS, TITLE, character.xp));
        nodes.Add(lineBreak(STATS));
        nodes.Add(tab(STATS));
        nodes.Add(text(STATS, LABEL, table(0x48E09925))); // "Needed for next level: "
        nodes.Add(bigNumber(STATS, TITLE, world.levelSystem.experienceToNextLevel(character)));
        nodes.Add(lineBreak(STATS));
        nodes.Add(tab(STATS));
        nodes.Add(text(STATS, LABEL, table(0xC0004BC9))); // "Available XP: "
        nodes.Add(bigNumber(STATS, TITLE, character.xpAvailable));
        nodes.Add(lineBreak(STATS));
    }

    private static StringInfo? speciesName(SpeciesType species) => species switch {
        SpeciesType.Human => table(0x7C8E3F7D),
        SpeciesType.Lugian => table(0xFCDEBE8E),
        SpeciesType.Tumerok => table(0x890E7CDE),
        SpeciesType.Empyrean => table(0x6C89BCFE),
        SpeciesType.Drudge => literal("Drudge"),
        _ => null,
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Everything else: NPCs, portals, stones, signs, lifestones, and items
    // ---------------------------------------------------------------------------------------------------------------

    private void addObject(List<ExaminationDataNode> nodes, WorldObject obj) {
        StringInfo? description = describe(obj);
        WeenieType type = (WeenieType)obj.getQ(IntStat.WeenieType);
        bool item = obj.takeable || obj.containerId != InstanceId.NULL || obj.equipperId != InstanceId.NULL
            || obj.validInvLocs != InvLoc.None || (type.HasFlag(WeenieType.Item)
                && type is not (WeenieType.Lifestone or WeenieType.Portal or WeenieType.SceneryObject or WeenieType.Door
                    or WeenieType.Shard or WeenieType.Mine or WeenieType.Forge));

        // An icon for things you can carry, and for portals (the capture showed one on both)
        if (obj.iconDid != DataId.NULL && (item || obj.packageType.ToString().Contains("Portal"))) {
            nodes.Add(image(HEADER, obj.iconDid));
        }

        nodes.Add(text(HEADER, TITLE, nameOf(obj)));
        nodes.Add(lineBreak(HEADER));

        if (item && obj.value > 0) {
            nodes.Add(text(HEADER, LABEL, table(0x2F3E4575))); // "Value: "
            nodes.Add(text(HEADER, TITLE, table(0xA828186C, (VAR_VALUE, obj.value))));
            nodes.Add(lineBreak(HEADER));
        }

        if (description != null) {
            nodes.Add(text(HEADER, LABEL, description));
            nodes.Add(lineBreak(HEADER));
        }

        if (!item) {
            return;
        }

        nodes.Add(text(HEADER, LABEL, literal("")));
        nodes.Add(lineBreak(HEADER));

        if (obj.quantity > 1) {
            nodes.Add(text(STATS, LABEL, literal("Quantity: ")));
            nodes.Add(number(STATS, TITLE, obj.quantity));
            nodes.Add(lineBreak(STATS));
        }
        if (obj.durabilityMax > 0) {
            nodes.Add(text(STATS, LABEL, literal("Durability: ")));
            nodes.Add(text(STATS, TITLE, literal($"{obj.durability} / {obj.durabilityMax}")));
            nodes.Add(lineBreak(STATS));
        }

        addWeaponStats(nodes, obj);
        addArmorStats(nodes, obj);
        addItemEffects(nodes, obj);
        addRequirements(nodes, obj);
    }

    /// <summary>
    /// Damage / delay / vigor cost, from the item's own recorded numbers. (The real server generated these per item; items from
    /// the game's definitions only carry what the definition records, so lines without a number are left out rather than invented.)
    /// </summary>
    private static void addWeaponStats(List<ExaminationDataNode> nodes, WorldObject item) {
        int damage = item.getQ(IntStat.Weapon_Damage);
        int delay = item.getQ(IntStat.Weapon_Speed);
        int vigorCost = item.getQ(IntStat.VigorCost);
        if (damage <= 0 && delay <= 0 && vigorCost <= 0) {
            return;
        }

        nodes.Add(text(STATS, LABEL, table(0xA4CC8C3D))); // "Weapon Stats:"
        nodes.Add(lineBreak(STATS));
        if (damage > 0) {
            float spread = item.getQ(FloatStat.Weapon_Variance);
            int min = Math.Max(1, (int)MathF.Round(damage * (1.0f - spread)));
            int max = Math.Max(min, (int)MathF.Round(damage * (1.0f + spread)));
            nodes.Add(tab(STATS));
            nodes.Add(text(STATS, LABEL, table(0x9B6C373C))); // "Damage: "
            nodes.Add(text(STATS, TITLE, table(0xA8621B86, (VAR_DAMAGE_MIN, min), (VAR_DAMAGE_MAX, max))));
            nodes.Add(lineBreak(STATS));
        }
        if (delay > 0) {
            nodes.Add(tab(STATS));
            nodes.Add(text(STATS, LABEL, table(0x3B4D51EA))); // "Combat Delay: "
            nodes.Add(number(STATS, TITLE, delay));
            nodes.Add(lineBreak(STATS));
        }
        if (vigorCost > 0) {
            nodes.Add(tab(STATS));
            nodes.Add(text(STATS, LABEL, table(0x157A4FEA))); // "Vigor Cost: "
            nodes.Add(number(STATS, TITLE, vigorCost));
            nodes.Add(lineBreak(STATS));
        }
    }

    private static void addArmorStats(List<ExaminationDataNode> nodes, WorldObject item) {
        InvLoc bodySlots = InvLoc.Head | InvLoc.Torso | InvLoc.Back | InvLoc.Legs | InvLoc.Feet | InvLoc.Hands;
        InvLoc slots = item.validInvLocs & bodySlots;
        if (slots == InvLoc.None) {
            return;
        }

        int armor = item.getQ(IntStat.ArmorLevel);
        int delay = item.getQ(IntStat.CombatDelay);

        nodes.Add(text(STATS, LABEL, table(0x1B6A40D9))); // "Armor Stats:"
        nodes.Add(lineBreak(STATS));
        if (armor > 0) {
            nodes.Add(tab(STATS));
            nodes.Add(text(STATS, LABEL, table(0x8FC39362))); // "Armor Rating: "
            nodes.Add(number(STATS, TITLE, armor));
            nodes.Add(lineBreak(STATS));
        }
        if (delay > 0) {
            nodes.Add(tab(STATS));
            nodes.Add(text(STATS, LABEL, table(0x3B4D51EA))); // "Combat Delay: "
            nodes.Add(number(STATS, TITLE, delay));
            nodes.Add(lineBreak(STATS));
        }
        nodes.Add(tab(STATS));
        nodes.Add(text(STATS, LABEL, table(0x67C3AEF9))); // "Worn on:"
        nodes.Add(lineBreak(STATS));
        nodes.Add(tab(STATS));
        nodes.Add(tab(STATS));
        nodes.Add(text(STATS, TITLE, literal(slots.ToString().Replace(", ", " and "))));
        nodes.Add(lineBreak(STATS));
    }

    private void addItemEffects(List<ExaminationDataNode> nodes, WorldObject item) {
        foreach (EffectRecord record in item.effectRegistry.effectInfo.Values) {
            if (record.effect == null) {
                continue;
            }
            try {
                if (world.contentManager.getWeenieState(record.effect.wstateDid).package is Effect effect && effect.name != null
                    && !effect.internalFlags.HasFlag(Effect.InternalFlag.IsClientNoUI)) {
                    nodes.Add(text(STATS, LABEL, literal("Effect: ")));
                    nodes.Add(text(STATS, TITLE, effect.name));
                    nodes.Add(lineBreak(STATS));
                    if (effect.description != null) {
                        nodes.Add(text(STATS, LABEL, effect.description));
                        nodes.Add(lineBreak(STATS));
                    }
                }
            } catch (Exception e) {
                Logs.GENERAL.warn(e, "Could not identify an item effect", "effect", record.effect.wstateDid);
            }
        }
    }

    private void addRequirements(List<ExaminationDataNode> nodes, WorldObject item) {
        int minLevel = item.getQ(IntStat.Usage_MinLevel);
        int lore = item.getQ(IntStat.Usage_RequiredArcaneLore);
        List<string> races = races_(item);
        SkillId skill1 = (SkillId)item.getQ(IntStat.Usage_RequiredSkill1);
        SkillId skill2 = (SkillId)item.getQ(IntStat.Usage_RequiredSkill2);
        if (minLevel <= 0 && races.Count == 0 && lore <= 0 && skill1 == default && skill2 == default) {
            return;
        }

        nodes.Add(lineBreak(REQUIREMENTS));
        nodes.Add(text(REQUIREMENTS, LABEL, table(0x4F39B658))); // "Requirements for Use:"
        nodes.Add(lineBreak(REQUIREMENTS));
        if (minLevel > 0) {
            nodes.Add(tab(REQUIREMENTS));
            nodes.Add(text(REQUIREMENTS, LABEL, table(0x161EC7C0))); // "Minimum Level: "
            nodes.Add(number(REQUIREMENTS, TITLE, minLevel));
            nodes.Add(lineBreak(REQUIREMENTS));
        }
        if (races.Count > 0) {
            nodes.Add(tab(REQUIREMENTS));
            nodes.Add(text(REQUIREMENTS, LABEL, table(0xE25D922D, (VAR_COUNT, races.Count)))); // "Must be ...:"
            nodes.Add(text(REQUIREMENTS, TITLE, literal(string.Join(", ", races))));
            nodes.Add(lineBreak(REQUIREMENTS));
        }
        if (lore > 0) {
            nodes.Add(tab(REQUIREMENTS));
            nodes.Add(text(REQUIREMENTS, LABEL, literal("Arcane Lore: ")));
            nodes.Add(number(REQUIREMENTS, TITLE, lore));
            nodes.Add(lineBreak(REQUIREMENTS));
        }
        addRequiredSkill(nodes, skill1, item.getQ(IntStat.Usage_RequiredSkill1Rating));
        addRequiredSkill(nodes, skill2, item.getQ(IntStat.Usage_RequiredSkill2Rating));
        nodes.Add(text(REQUIREMENTS, LABEL, literal("")));
        nodes.Add(lineBreak(REQUIREMENTS));
    }

    private void addRequiredSkill(List<ExaminationDataNode> nodes, SkillId skillId, int rating) {
        if (!world.contentManager.tryGetSkill(skillId, out Skill? skill) || skill?.name == null) {
            return;
        }
        nodes.Add(tab(REQUIREMENTS));
        nodes.Add(text(REQUIREMENTS, LABEL, literal("Required skill: ")));
        nodes.Add(text(REQUIREMENTS, TITLE, skill.name));
        if (rating > 0) {
            nodes.Add(text(REQUIREMENTS, TITLE, literal($" ({rating})")));
        }
        nodes.Add(lineBreak(REQUIREMENTS));
    }

    /// <summary>The races that can wear a piece of clothing or armor (empty = everyone / not recorded).</summary>
    private List<string> races_(WorldObject item) {
        List<string> result = new();
        try {
            WState state = world.contentManager.getWeenieStateFromEntityDid(item.entityDid);
            if (state.package is Clothing clothing && clothing.wornAppearanceDidHash != null) {
                HashSet<SpeciesType> species = new(clothing.wornAppearanceDidHash.Keys.Select(k => k.species).Where(s => s != SpeciesType.Undef));
                if (species.Count > 0 && species.Count < 5) {
                    result.AddRange(species.OrderBy(s => (uint)s).Select(s => s.ToString()));
                }
            }
        } catch (Exception) {
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The name the client was shown for the object (objects placed from the capture carry theirs in the creation data).</summary>
    private static StringInfo nameOf(WorldObject obj) => obj.qualities.weenieDesc?.name ?? obj.name ?? literal("Unknown");

    /// <summary>The object's own description (string 0x6C459364 in its name's text table), if it has one.</summary>
    private StringInfo? describe(WorldObject obj) {
        StringInfo? name = obj.qualities.weenieDesc?.name ?? obj.name;
        // Some names are a template ("{{item}} ") wrapped around the real name; the description is in the real name's table
        if (name?.variables != null && name.variables.TryGetValue((StringVariable)VAR_NAME, out StringInfoData? inner) && inner.valString != null && inner.valString.tableDid != DataId.NULL) {
            name = inner.valString;
        }
        if (name == null || name.tableDid == DataId.NULL) {
            return null;
        }
        try {
            StringTable stringTable = world.contentManager.getStringTable(name.tableDid);
            if (stringTable.strings.TryGetValue(new StringId(DESCRIPTION_STRING), out StringTableString? entry) && entry.strings.Count > 0 && entry.strings[0].Length > 0) {
                return new StringInfo(name.tableDid, new StringId(DESCRIPTION_STRING));
            }
        } catch (Exception) {
        }
        return null;
    }

    private static ExaminationDataNode text(uint order, uint style, StringInfo value) => new() {
        type = ExaminationDataNode.DataType.String,
        order = order,
        appearanceId = style,
        valString = value,
    };

    private static ExaminationDataNode number(uint order, uint style, int value) => new() {
        type = ExaminationDataNode.DataType.Int,
        order = order,
        appearanceId = style,
        valInt = value,
    };

    private static ExaminationDataNode bigNumber(uint order, uint style, long value) => new() {
        type = ExaminationDataNode.DataType.LongInt,
        order = order,
        appearanceId = style,
        valLongInt = value,
    };

    private static ExaminationDataNode lineBreak(uint order) => new() {
        type = ExaminationDataNode.DataType.Break,
        order = order,
    };

    private static ExaminationDataNode tab(uint order) => new() {
        type = ExaminationDataNode.DataType.Tab,
        order = order,
    };

    private static ExaminationDataNode image(uint order, DataId did) => new() {
        type = ExaminationDataNode.DataType.Image,
        order = order,
        valDataID = did,
    };

    private static StringInfo literal(string value) => new(value);

    /// <summary>An entry of the client's main text table, with optional number variables.</summary>
    private static StringInfo table(uint stringId, params (uint key, int value)[] variables) {
        if (variables.Length == 0) {
            return new StringInfo(TEXT, new StringId(stringId));
        }
        Dictionary<StringVariable, StringInfoData> vars = new();
        foreach ((uint key, int value) in variables) {
            vars[(StringVariable)key] = new StringInfoData(value);
        }
        return new StringInfo(TEXT, new StringId(stringId), vars);
    }

    /// <summary>An entry of the client's main text table with text variables.</summary>
    private static StringInfo tableText(uint stringId, params (uint key, StringInfo value)[] variables) {
        Dictionary<StringVariable, StringInfoData> vars = new();
        foreach ((uint key, StringInfo value) in variables) {
            vars[(StringVariable)key] = new StringInfoData(value);
        }
        return new StringInfo(TEXT, new StringId(stringId), vars);
    }
}

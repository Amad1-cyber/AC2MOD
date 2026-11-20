using AC2RE.Definitions;
using System;
using System.Collections.Generic;

namespace AC2RE.Server;

/// <summary>
/// MOD2: experience and character levels.
///
/// The game's own data has a character level table (portal.dat 0x70000380): for each level the total experience it needs, the
/// maximum health and vigor, and the skill credits it grants. (It is stored highest level first.) It matches the real server in
/// your capture exactly: level 4 at 271 experience with 16 to go for level 5 (287), health 160 at level 3 and 190 at level 4.
///
/// What the real server sent when a character gained a level (copied from the capture): the experience message, an updated skill
/// repository (credits), the "levelup" effect on the character, and private updates of TotalXP, AvailableXP, Level,
/// Health_CachedMax, Vigor_CachedMax and the (now full) current health and vigor. The stat updates go out by themselves through
/// StatConfig's private sync.
/// </summary>
internal class LevelSystem {

    // The experience message's text and its one variable, as in the capture's DisplayStringInfo
    private static readonly DataId XP_MESSAGE_TABLE = new(0x2500000B);
    private static readonly StringId XP_MESSAGE_ID = new(0xCBE599B5);
    private const uint XP_MESSAGE_AMOUNT_VARIABLE = 2973761536;

    private readonly World world;

    public LevelSystem(World world) {
        this.world = world;
    }

    private LevelTable table => world.contentManager.getLevelTable();

    public int maxLevel => (int)table.maxLevel;

    /// <summary>The table row of a level (the table lists the highest level first).</summary>
    private LevelData row(int level) {
        int index = Math.Clamp(maxLevel - level, 0, table.map.Count - 1);
        return table.map[index];
    }

    public long experienceForLevel(int level) {
        ulong xp = row(Math.Clamp(level, 1, maxLevel)).xp;
        return xp > long.MaxValue ? long.MaxValue : (long)xp;
    }

    public int levelForExperience(long totalExperience) {
        for (int level = maxLevel; level > 1; level--) {
            if (totalExperience >= experienceForLevel(level)) {
                return level;
            }
        }
        return 1;
    }

    /// <summary>Experience still needed for the next level (0 at the highest level).</summary>
    public long experienceToNextLevel(WorldObject character) {
        int level = Math.Max(character.level, 1);
        return level >= maxLevel ? 0 : Math.Max(experienceForLevel(level + 1) - character.xp, 0);
    }

    /// <summary>A new character: its level follows from its starting experience and it starts with full health and vigor.</summary>
    public void initializeCharacter(WorldObject character) {
        character.level = levelForExperience(character.xp);
        applyVitals(character, refill: true);
    }

    /// <summary>Give experience (a kill, a quest...). Tells the player and levels the character up if it earned it.</summary>
    public void awardExperience(WorldObject character, long amount) {
        if (amount <= 0) {
            return;
        }

        character.xp += amount;
        character.xpAvailable += amount;

        if (character.player != null) {
            world.playerManager.send(character.player, new DisplayStringInfoCEvt {
                text = new StringInfo(XP_MESSAGE_TABLE, XP_MESSAGE_ID, new Dictionary<StringVariable, StringInfoData> {
                    { (StringVariable)XP_MESSAGE_AMOUNT_VARIABLE, new StringInfoData { type = StringInfoData.DataType.LInt, valLong = amount } },
                }),
                type = TextType.Standard,
            });
        }

        checkLevelUp(character);
    }

    /// <summary>
    /// Levels the character up if its experience says it should be higher. Used after every award and when a character enters the
    /// world (so a character that earned experience before levelling existed catches up).
    /// </summary>
    public void checkLevelUp(WorldObject character) {
        int oldLevel = Math.Max(character.level, 1);
        int newLevel = levelForExperience(character.xp);

        if (newLevel > oldLevel) {
            uint credits = 0;
            for (int level = oldLevel + 1; level <= newLevel; level++) {
                credits += row(level).skillCredits;
            }

            character.level = newLevel;
            applyVitals(character, refill: true);

            if (credits > 0) {
                character.skillRepo.skillCredits += credits;
            }
            character.sendUpdateSkillRepo();

            character.doFx(FxId.levelup, 1.0f);

            if (character.player != null) {
                string gained = credits > 0 ? $" You gained {credits} skill credit{(credits == 1 ? "" : "s")}." : "";
                world.playerManager.send(character.player, new DisplayStringInfoCEvt {
                    text = new StringInfo($"You have reached level {newLevel}!{gained}\n"),
                    type = TextType.Standard,
                });
            }

            Logs.GENERAL.info("Character levelled up", "character", character.id, "from", oldLevel, "to", newLevel);
        } else {
            applyVitals(character, refill: false);
        }
    }

    /// <summary>Health / vigor maximums for the character's level (the table's values). New maximums are only filled up on a level-up.</summary>
    private void applyVitals(WorldObject character, bool refill) {
        LevelData data = row(Math.Max(character.level, 1));
        int health = (int)data.health;
        int vigor = (int)data.vigor;

        if (character.healthMax != health) {
            character.healthMax = health;
        }
        if (character.vigorMax != vigor) {
            character.vigorMax = vigor;
        }

        if (refill) {
            character.health = health;
            character.vigor = vigor;
        } else {
            if (character.health > health) {
                character.health = health;
            }
            if (character.vigor > vigor) {
                character.vigor = vigor;
            }
        }
    }
}

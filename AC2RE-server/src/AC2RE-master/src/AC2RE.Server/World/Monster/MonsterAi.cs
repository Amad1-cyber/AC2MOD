using AC2RE.Definitions;
using AC2RE.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AC2RE.Server;

/// <summary>
/// MOD2: server-side creature behavior, so the enemies "live, act and drop loot when they die".
///
/// Everything here is copied from what the real server was seen doing in the user's capture (see tools\MapDbGen "track" mode):
///  - a creature that notices a player switches to the "combat_monster" mode and runs at them; its position is broadcast
///    4 times a second with a forward "doMotion" and a heading, and it stops about 3 m from its target;
///  - it swings every 1.5 - 2.2 s: a DoStory with Melee_Weak1 / Melee_Normal1 / Melee_Strong1 (skills Mn_Me_Normal / Strong /
///    Stronger) and one "Damage" hook (Hit with a health change, or Evade with none);
///  - when the target gets too far away it switches back to "peace" and runs home;
///  - when it dies it announces the killer (already done by WorldObject.applyDamage), plays its death animation, and its
///    corpse held loot. Here the loot is dropped on the ground beside the body (take it with a double click) because the
///    corpse-container window can't be tested without the game client.
/// A creature is removed shortly after death and respawns at its home spot a while later.
///
/// Runs on the server's single game-loop thread (World.tick), like all message handling, so no locking is needed.
/// </summary>
internal class MonsterAi {

    private enum State { Idle, Combat, Return, Dead }

    private sealed class Monster {
        public WorldObject obj = null!;
        public Position home = null!;
        public float homeHeading;
        public bool canMove; // outdoor creatures only: indoor cell ids can't be derived from coordinates
        public ModeId spawnMode;

        // The creature's own numbers from the game's entity definition (see register)
        public int level = 1;
        public float weaponDamage; // 0 = not recorded
        public float damageSpread = 0.4f;
        public float aggroRange = AGGRO_RANGE;
        public float leashRange = LEASH_RANGE;
        public bool passive; // training dummies and critters never start a fight

        public State state = State.Idle;
        public InstanceId targetId = InstanceId.NULL;
        public bool inCombatMode;
        public bool moving;

        public double nextScan;
        public double nextStep;
        public double lastStep;
        public double nextAttack;

        public double pendingHitAt; // 0 = nothing pending; the damage lands when the swing animation connects
        public int pendingHitDamage;
        public InstanceId pendingHitTarget = InstanceId.NULL;

        public double diedAt;
        public bool bodyGone;
        public double nextRespawnTry;
    }

    // Outdoor landblocks are 8 x 8 cells of 20 m (checked against the capture: cell id = x_cell * 8 + y_cell + 1)
    private const float LANDBLOCK_SIZE = 160.0f;
    private const float CELL_SIZE = 20.0f;

    private const float AGGRO_RANGE = 11.0f; // a player this close wakes a creature up
    private const float LOSE_RANGE = 32.0f; // the creature gives up when its target is further away than this
    private const float LEASH_RANGE = 45.0f; // ... or when it has been lured this far from home
    private const float IMMOBILE_AGGRO_RANGE = 5.0f; // creatures that can't move (indoors) only engage a player this close
    private const float IMMOBILE_LOSE_RANGE = 9.0f;
    private const float ARRIVE_SLACK = 0.25f; // within this much of the stop distance it counts as arrived (no endless tiny steps)
    private const float MELEE_STOP = 2.6f; // it stops running this far from its target
    private const float MELEE_RANGE = 3.6f; // and swings when within this distance
    private const float RUN_SPEED = 5.5f; // m/s; the capture's creatures ran at about 5.9

    private const double MOVE_INTERVAL = 0.25; // the real server sent creature positions 4 times a second
    private const double SCAN_INTERVAL = 0.5;
    private const double HIT_DELAY = 0.45; // swing animation time before the damage lands
    private const double LOOT_LIFETIME = 300.0;
    private const float EVADE_CHANCE = 0.2f;
    private const long XP_BASE = 10;
    private const long XP_PER_LEVEL = 11;

    private static readonly Vector3 FORWARD = new(0.0f, 1.0f, 0.0f); // "doMotion" of a creature running forward

    // Items the real server put into creature corpses in the capture (entity ids as seen in CreateObject messages)
    private static readonly uint[] LOOT_DIDS = {
        0x4700215D, // SwordTumerokHooksword
        0x4700209C, // SharpstickSkewer
        0x47001D08, // AxeLugianHatchet
        0x47002116, // StaffApprentice
        0x47001DAF, // FrogstaveInitiatesFocus
        0x4700214C, // SwordLugianShort
        0x4700207F, // ScepterLugian
        0x47001D4A, // BowHumanSmall
        0x47002151, // SwordPeasant
        0x47001C1A, // ShieldLugianStone
        0x47000D72, // GlovesHumanPadded
        0x47000DB3, // GauntletsLugianQuilted
        0x47000E79, // RobeHuman
        0x47000E04, // LeggingsTumerokInitiate
        0x47000D79, // GreavesHumanPadded
        0x47000DF4, // BreastplateTumerokInitiate
    };

    private readonly World world;
    private readonly Dictionary<InstanceId, Monster> monsters = new();
    private readonly List<(WorldObject item, double expires)> groundLoot = new();
    private readonly Dictionary<InstanceId, double> lastHurt = new();
    private readonly List<uint> lootPool = new(LOOT_DIDS);
    private double nextHousekeeping;
    private double nextErrorLog;

    // How long a dead creature's body stays, and how long until it comes back (instance fields so the self-test can shorten them)
    private double bodyRemoveDelay = 25.0;
    private double respawnDelay = 60.0;

    public MonsterAi(World world) {
        this.world = world;
    }

    public bool isCreature(InstanceId id) => monsters.ContainsKey(id);

    /// <summary>A creature object was loaded from the map data: start controlling it.</summary>
    public void register(WorldObject obj) {
        // The capture's CreateObject for a live creature carried a velocity scale of 1 and the "peace" mode (static
        // scenery has neither); a creature that runs around needs them so the client animates it moving
        obj.physics.velScale = 1.0f;
        obj.physics.modeId = ModeId.peace;

        Monster m = new() {
            obj = obj,
            home = new Position { cell = obj.pos.cell, frame = obj.pos.frame },
            homeHeading = obj.heading,
            canMove = obj.pos.cell.indoorCellId == 0,
            spawnMode = obj.physics.modeId,
            nextScan = world.serverTime.time + Random.Shared.NextDouble(),
        };

        // The game's creature definitions carry the real numbers (Drudge Digger: level 1, damage 5, notices you at 30 m;
        // Vermin Pilferer: level 2, damage 9, notices you at 15 m; Straw Drudge: a training dummy that does 1 damage)
        m.level = Math.Max(obj.getQ(IntStat.Level), 1);
        m.weaponDamage = obj.getQ(IntStat.Weapon_Damage);
        float spread = obj.getQ(FloatStat.Weapon_Variance);
        if (spread > 0.0f) {
            m.damageSpread = Math.Clamp(spread, 0.1f, 0.9f);
        }
        float perception = obj.getQ(FloatStat.AI_PerceptionRadius);
        if (perception > 0.0f) {
            m.aggroRange = Math.Clamp(perception, 6.0f, 20.0f);
        }
        float homesick = obj.getQ(FloatStat.AI_HomesickRadius);
        if (homesick > 0.0f) {
            m.leashRange = Math.Clamp(homesick, LEASH_RANGE * 0.55f, LEASH_RANGE);
        }
        m.passive = obj.packageType == PackageType.InanimateMonsterTemplate || (m.weaponDamage > 0.0f && m.weaponDamage <= 1.0f);

        monsters[obj.id] = m;
    }

    /// <summary>Called from WorldObject.applyDamage: a creature was hurt. It turns on whoever hit it.</summary>
    public void onDamaged(WorldObject victim, WorldObject? attacker) {
        if (attacker?.player != null && victim.health > 0 && monsters.TryGetValue(victim.id, out Monster? m) && !m.passive && m.state != State.Dead && !victim.dead) {
            if (m.state != State.Combat) {
                aggro(m, attacker, world.serverTime.time);
            }
        }
    }

    /// <summary>Called from WorldObject.applyDamage when a creature's health reached zero.</summary>
    public void onKilled(WorldObject victim, WorldObject? killer) {
        if (monsters.TryGetValue(victim.id, out Monster? m) && m.state != State.Dead) {
            beginDeath(m, killer, world.serverTime.time);
        }
    }

    public void update() {
        double now = world.serverTime.time;

        if (!selfTestDone) {
            selfTestStep(now);
        }

        List<WorldObject> characters = new();
        foreach (Player player in world.playerManager.players) {
            if (player.characterId != InstanceId.NULL && world.objectManager.tryGet(player.characterId, out WorldObject? character) && character.inWorld) {
                characters.Add(character);
            }
        }

        // Snapshot: new landblocks (and so new creatures) can load while we work
        foreach (Monster m in monsters.Values.ToList()) {
            try {
                step(m, now, characters);
            } catch (Exception e) {
                if (now >= nextErrorLog) {
                    nextErrorLog = now + 10.0;
                    Logs.GENERAL.warn(e, "Creature AI error", "creature", m.obj.id);
                }
            }
        }

        if (now >= nextHousekeeping) {
            nextHousekeeping = now + 1.0;
            try {
                housekeeping(now, characters);
            } catch (Exception e) {
                Logs.GENERAL.warn(e, "Creature housekeeping error");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Per-creature state machine
    // ---------------------------------------------------------------------------------------------------------------

    private void step(Monster m, double now, List<WorldObject> characters) {
        WorldObject? current = world.objectManager.get(m.obj.id);
        if (current == null || !ReferenceEquals(current, m.obj)) {
            monsters.Remove(m.obj.id); // the landblock was reloaded and made a fresh object, or this one was deleted
            return;
        }

        if (m.state != State.Dead && m.obj.dead) {
            beginDeath(m, null, now);
        }

        switch (m.state) {
            case State.Idle:
                if (now >= m.nextScan) {
                    m.nextScan = now + SCAN_INTERVAL;
                    scanForTarget(m, now, characters);
                }
                break;
            case State.Combat:
                combat(m, now);
                break;
            case State.Return:
                goHome(m, now);
                break;
            case State.Dead:
                dead(m, now, characters);
                break;
        }
    }

    private void scanForTarget(Monster m, double now, List<WorldObject> characters) {
        if (!m.obj.inWorld || m.passive) {
            return;
        }

        WorldObject? best = null;
        float bestDist = m.canMove ? m.aggroRange : IMMOBILE_AGGRO_RANGE;
        foreach (WorldObject character in characters) {
            if (character.dead || !tryDelta(m.obj.pos, character.pos, out Vector3 delta) || MathF.Abs(delta.Z) > 12.0f) {
                continue;
            }
            float dist = horizontal(delta);
            if (dist < bestDist) {
                bestDist = dist;
                best = character;
            }
        }

        if (best != null) {
            aggro(m, best, now);
        }
    }

    private void aggro(Monster m, WorldObject target, double now) {
        m.state = State.Combat;
        m.targetId = target.id;
        m.nextStep = now;
        m.lastStep = now;
        m.nextAttack = Math.Max(m.nextAttack, now + 0.8);

        if (!m.inCombatMode) {
            m.obj.doMode(ModeId.combat_monster);
            m.inCombatMode = true;
        }
    }

    private void combat(Monster m, double now) {
        if (m.pendingHitAt > 0.0 && now >= m.pendingHitAt) {
            landHit(m, now);
        }

        if (now < m.nextStep) {
            return;
        }
        m.nextStep = now + MOVE_INTERVAL;
        double dt = Math.Min(now - m.lastStep, 0.6);
        m.lastStep = now;

        if (!world.objectManager.tryGet(m.targetId, out WorldObject? target) || !isValidTarget(target)
            || !tryDelta(m.obj.pos, target.pos, out Vector3 delta)) {
            disengage(m, now);
            return;
        }

        float dist = horizontal(delta);
        float loseRange = m.canMove ? LOSE_RANGE : IMMOBILE_LOSE_RANGE;
        if (dist > loseRange || (tryDelta(m.home, m.obj.pos, out Vector3 fromHome) && horizontal(fromHome) > m.leashRange)) {
            disengage(m, now);
            return;
        }

        float bearing = MathF.Atan2(delta.X, delta.Y) * MathUtil.RAD_TO_DEG;
        if (dist > MELEE_STOP + ARRIVE_SLACK && m.canMove) {
            runToward(m, delta, dist - MELEE_STOP, (float)dt, bearing);
        } else {
            standAndFace(m, bearing);
        }

        if (dist <= MELEE_RANGE && now >= m.nextAttack) {
            swing(m, target, now, bearing);
        }
    }

    /// <summary>A target must be in the world, alive and still played by a connected player (the self-test's stand-in is the exception).</summary>
    private bool isValidTarget(WorldObject target) {
        if (!target.inWorld || target.dead) {
            return false;
        }
        return world.playerManager.tryGet(target.id, out Player? _) || (selfTest != null && ReferenceEquals(selfTest.dummy, target));
    }

    private void goHome(Monster m, double now) {
        if (now < m.nextStep) {
            return;
        }
        m.nextStep = now + MOVE_INTERVAL;
        double dt = Math.Min(now - m.lastStep, 0.6);
        m.lastStep = now;

        if (!m.canMove || !tryDelta(m.obj.pos, m.home, out Vector3 delta) || horizontal(delta) <= 1.0f) {
            m.obj.pos = m.home;
            m.obj.motion = Vector3.Zero;
            m.moving = false;
            m.state = State.Idle;
            m.nextScan = now + 2.0;
            m.obj.health = m.obj.healthMax;
            m.obj.vigor = m.obj.vigorMax;
            return;
        }

        float dist = horizontal(delta);
        runToward(m, delta, dist, (float)dt, MathF.Atan2(delta.X, delta.Y) * MathUtil.RAD_TO_DEG);
    }

    private void dead(Monster m, double now, List<WorldObject> characters) {
        if (!m.bodyGone && now >= m.diedAt + bodyRemoveDelay) {
            m.obj.leaveWorld();
            m.bodyGone = true;
        }

        if (m.bodyGone && now >= m.diedAt + respawnDelay && now >= m.nextRespawnTry) {
            foreach (WorldObject character in characters) {
                if (tryDelta(m.home, character.pos, out Vector3 delta) && horizontal(delta) < 8.0f) {
                    m.nextRespawnTry = now + 5.0; // don't pop up on top of a player
                    return;
                }
            }
            respawn(m, now);
        }
    }

    private void disengage(Monster m, double now) {
        m.targetId = InstanceId.NULL;
        m.pendingHitAt = 0.0;
        if (m.moving) {
            m.obj.motion = Vector3.Zero;
            m.moving = false;
        }
        if (m.inCombatMode) {
            m.obj.doMode(ModeId.peace);
            m.inCombatMode = false;
        }
        m.state = State.Return;
        m.nextStep = now;
        m.lastStep = now;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Movement
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Run <paramref name="travel"/> metres (at most RUN_SPEED * dt) along <paramref name="delta"/>.</summary>
    private void runToward(Monster m, Vector3 delta, float travel, float dt, float bearing) {
        float dist = horizontal(delta);
        float run = MathF.Min(RUN_SPEED * dt, travel);
        if (dist < 0.001f || run <= 0.0f) {
            standAndFace(m, bearing);
            return;
        }

        // Height: follow the straight line to the destination (the server has no terrain data to look the ground up)
        float t = run / dist;
        m.obj.offset = moved(m.obj.pos.cell, m.obj.pos.frame.pos, new Vector3(delta.X * t, delta.Y * t, delta.Z * t));
        m.obj.heading = bearing;
        m.obj.motion = FORWARD;
        m.moving = true;
    }

    private void standAndFace(Monster m, float bearing) {
        if (m.moving) {
            m.obj.motion = Vector3.Zero;
            m.moving = false;
            m.obj.heading = bearing; // marks the position dirty, so the stop is broadcast
        } else if (angleDifference(m.obj.heading, bearing) > 8.0f) {
            m.obj.heading = bearing;
        }
    }

    /// <summary>The position after moving by <paramref name="step"/>, with the cell id recomputed (outdoors).</summary>
    private static PositionOffset moved(CellId cell, Vector3 pos, Vector3 step) {
        Vector3 np = pos + step;
        if (cell.indoorCellId != 0) {
            return new(cell, np);
        }

        byte lbX = cell.landblockX;
        byte lbY = cell.landblockY;
        while (np.X >= LANDBLOCK_SIZE && lbX < 255) {
            np.X -= LANDBLOCK_SIZE;
            lbX++;
        }
        while (np.X < 0.0f && lbX > 0) {
            np.X += LANDBLOCK_SIZE;
            lbX--;
        }
        while (np.Y >= LANDBLOCK_SIZE && lbY < 255) {
            np.Y -= LANDBLOCK_SIZE;
            lbY++;
        }
        while (np.Y < 0.0f && lbY > 0) {
            np.Y += LANDBLOCK_SIZE;
            lbY--;
        }

        int cellX = Math.Clamp((int)(np.X / CELL_SIZE), 0, 7);
        int cellY = Math.Clamp((int)(np.Y / CELL_SIZE), 0, 7);
        return new(new CellId(lbX, lbY, 0, (byte)(cellX * 8 + cellY + 1)), np);
    }

    /// <summary>Vector from <paramref name="from"/> to <paramref name="to"/> in metres, if both are in reach of each other.</summary>
    private static bool tryDelta(Position from, Position to, out Vector3 delta) {
        Vector3 a = from.frame.pos;
        Vector3 b = to.frame.pos;

        if (from.cell.landblockId == to.cell.landblockId) {
            delta = b - a;
            return true;
        }

        // Different landblocks only make sense outdoors, between neighbours
        int dx = to.cell.landblockX - from.cell.landblockX;
        int dy = to.cell.landblockY - from.cell.landblockY;
        if (from.cell.indoorCellId != 0 || to.cell.indoorCellId != 0 || Math.Abs(dx) > 1 || Math.Abs(dy) > 1) {
            delta = default;
            return false;
        }

        delta = new Vector3(b.X - a.X + dx * LANDBLOCK_SIZE, b.Y - a.Y + dy * LANDBLOCK_SIZE, b.Z - a.Z);
        return true;
    }

    private static float horizontal(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    private static float angleDifference(float a, float b) {
        float d = (a - b) % 360.0f;
        if (d < -180.0f) {
            d += 360.0f;
        } else if (d > 180.0f) {
            d -= 360.0f;
        }
        return MathF.Abs(d);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Attacking
    // ---------------------------------------------------------------------------------------------------------------

    private void swing(Monster m, WorldObject target, double now, float bearing) {
        m.nextAttack = now + 1.5 + Random.Shared.NextDouble() * 0.7;

        float roll = Random.Shared.NextSingle();
        BehaviorId behavior;
        SkillId skill;
        float power;
        if (roll < 0.45f) {
            behavior = BehaviorId.Melee_Weak1;
            skill = SkillId.Mn_Me_Normal;
            power = 0.7f;
        } else if (roll < 0.8f) {
            behavior = BehaviorId.Melee_Normal1;
            skill = SkillId.Mn_Me_Strong;
            power = 1.0f;
        } else {
            behavior = BehaviorId.Melee_Strong1;
            skill = SkillId.Mn_Me_Stronger;
            power = 1.4f;
        }

        // The creature's recorded weapon damage, spread by its recorded variance (Vermin Pilferer: 9 +/- 50%, so single hits of
        // roughly 3 to 19 - the range the real server's creatures hit for in the capture)
        bool evaded = Random.Shared.NextSingle() < target.defenseEvadeChance(SkillId.Com_Me_MeleeDefense, EVADE_CHANCE);
        float baseDamage = m.weaponDamage > 0.0f ? m.weaponDamage : Math.Max(3, m.obj.healthMax / 6 + 2);
        float spread = 1.0f + m.damageSpread * (Random.Shared.NextSingle() * 2.0f - 1.0f);
        int damage = evaded ? 0 : Math.Max(1, (int)MathF.Round(baseDamage * power * spread));

        // Face the target and stop, then play the swing
        m.obj.motion = Vector3.Zero;
        m.moving = false;
        m.obj.heading = bearing;

        m.obj.doStory(new PhysicsStory {
            attackerId = m.obj.id,
            attackerBehavior = new BehaviorParams {
                behaviorId = behavior,
                timeScale = 1.17f + 0.25f * Random.Shared.NextSingle(),
                propagate = true,
            },
            skillId = skill,
            hooks = new List<StoryHookData> {
                new() {
                    type = StoryHookData.HookType.Damage,
                    targetId = target.id,
                    attackResult = evaded ? CombatResultType.Evade : CombatResultType.Hit,
                    animHookNumber = 1,
                    targetHealthChange = -damage,
                },
            },
        });

        m.pendingHitAt = now + HIT_DELAY;
        m.pendingHitDamage = damage;
        m.pendingHitTarget = target.id;
    }

    private void landHit(Monster m, double now) {
        int damage = m.pendingHitDamage;
        InstanceId targetId = m.pendingHitTarget;
        m.pendingHitAt = 0.0;

        if (damage > 0 && !m.obj.dead && world.objectManager.tryGet(targetId, out WorldObject? target) && !target.dead) {
            target.applyDamage(damage, m.obj);
            lastHurt[target.id] = now;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Death, loot, respawn
    // ---------------------------------------------------------------------------------------------------------------

    private void beginDeath(Monster m, WorldObject? killer, double now) {
        m.state = State.Dead;
        m.diedAt = now;
        m.targetId = InstanceId.NULL;
        m.pendingHitAt = 0.0;
        m.moving = false;
        m.inCombatMode = false;
        m.obj.motion = Vector3.Zero;

        try {
            dropLoot(m, killer, now);
        } catch (Exception e) {
            Logs.GENERAL.warn(e, "Could not drop loot", "creature", m.obj.id);
        }

        if (killer?.player != null) {
            try {
                // About 21 experience for a level 1 creature and 32 for a level 2 one (the capture showed +33 for a level 2 kill)
                world.levelSystem.awardExperience(killer, XP_BASE + XP_PER_LEVEL * m.level);
            } catch (Exception e) {
                Logs.GENERAL.warn(e, "Could not award experience");
            }
        }
    }

    private void dropLoot(Monster m, WorldObject? killer, double now) {
        bool dropsKey = PortalTable.tryGetLootKey(m.obj.id, out DataId key);
        float roll = Random.Shared.NextSingle();
        int count = dropsKey ? 1 : roll < 0.65f ? 1 : roll < 0.95f ? 2 : 3;

        List<string> names = new();
        for (int i = 0; i < count; i++) {
            WorldObject? item = dropsKey ? createLootItem(key) : createRandomLootItem();
            if (item == null) {
                continue;
            }

            float angle = Random.Shared.NextSingle() * MathF.PI * 2.0f;
            float radius = 1.0f + Random.Shared.NextSingle() * 1.3f;
            PositionOffset spot = moved(m.obj.pos.cell, m.obj.pos.frame.pos, new Vector3(MathF.Sin(angle) * radius, MathF.Cos(angle) * radius, 0.0f));
            item.pos = new Position { cell = spot.cell, frame = new Frame(spot.offset, Quaternion.Identity) };
            item.enterWorld();

            groundLoot.Add((item, now + LOOT_LIFETIME));
            names.Add(displayName(item, "an item"));
        }

        Logs.GENERAL.info("Creature died", "creature", m.obj.id, "loot", count, "dropped", names.Count);

        if (killer?.player != null && names.Count > 0) {
            world.playerManager.send(killer.player, new DisplayStringInfoMsg {
                text = new StringInfo($"The {displayName(m.obj, "creature")} dropped: {string.Join(", ", names)}. Double-click an item to pick it up.\n"),
                type = TextType.Standard,
            });
        }
    }

    private WorldObject? createRandomLootItem() {
        for (int attempt = 0; attempt < 4 && lootPool.Count > 0; attempt++) {
            uint did = lootPool[Random.Shared.Next(lootPool.Count)];
            try {
                return createLootItem(new DataId(did));
            } catch (Exception e) {
                Logs.GENERAL.warn(e, "Loot item can't be created; removed from the loot table", "entityDid", did);
                lootPool.Remove(did);
            }
        }
        return null;
    }

    private WorldObject createLootItem(DataId did) {
        // Ground loot only becomes persistent when a player picks it up.
        WorldObject item = world.objectManager.create(InstanceId.IdType.Dynamic, did);
        item.takeable = true;
        return item;
    }

    private void respawn(Monster m, double now) {
        WorldObject o = m.obj;
        o.dead = false;
        o.selectable = true;
        o.health = o.healthMax;
        o.vigor = o.vigorMax;
        o.motion = Vector3.Zero;
        o.physics.modeId = m.spawnMode;
        o.pos = m.home;

        m.state = State.Idle;
        m.bodyGone = false;
        m.nextScan = now + 2.0;
        m.nextAttack = 0.0;

        o.enterWorld();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Once a second: loot clean-up and vital regeneration
    // ---------------------------------------------------------------------------------------------------------------

    private void housekeeping(double now, List<WorldObject> characters) {
        for (int i = groundLoot.Count - 1; i >= 0; i--) {
            (WorldObject item, double expires) = groundLoot[i];
            if (item.deleted || item.containerId != InstanceId.NULL) {
                groundLoot.RemoveAt(i); // somebody picked it up
            } else if (now >= expires) {
                item.delete();
                groundLoot.RemoveAt(i);
            }
        }

        // Recover both vitals once per second after six seconds without taking damage.
        foreach (WorldObject character in characters) {
            if (character.dead || character.health <= 0) {
                continue;
            }
            if (lastHurt.TryGetValue(character.id, out double hurtAt) && now - hurtAt < 6.0) {
                continue;
            }
            if (character.health < character.healthMax) {
                character.health = Math.Min(character.healthMax, character.health + Math.Max(3, character.healthMax / 50));
            }
            if (character.vigor < character.vigorMax) {
                character.vigor = Math.Min(character.vigorMax, character.vigor + Math.Max(3, character.vigorMax / 50));
            }
        }
    }

    private string displayName(WorldObject obj, string fallback) {
        try {
            return obj.name != null ? world.contentManager.getString(obj.name) : fallback;
        } catch (Exception) {
            return fallback;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Self-test: a headless check of the whole creature life cycle, without needing the game client.
    // It runs once at server start if a file called "ai-selftest.flag" exists in the server folder (the flag is deleted
    // first), and writes logs\ai_selftest.txt. Nothing it creates is saved to the database.
    // ---------------------------------------------------------------------------------------------------------------

    private const string SELF_TEST_FLAG = "ai-selftest.flag";

    private sealed class SelfTest {
        public int phase;
        public double phaseStart;
        public double nextLog;
        public Monster m = null!;
        public WorldObject dummy = null!;
        public System.IO.StreamWriter log = null!;
        public int hitsSeen;
        public int lastDummyHealth;
        public Vector3 startPos;
        public float closestApproach = float.MaxValue;
        public bool respawned;
    }

    private SelfTest? selfTest;
    private bool selfTestDone;

    private void selfTestStep(double now) {
        if (selfTest == null) {
            if (!System.IO.File.Exists(SELF_TEST_FLAG)) {
                selfTestDone = true;
                return;
            }
            System.IO.File.Delete(SELF_TEST_FLAG);
            selfTest = new SelfTest {
                log = new System.IO.StreamWriter(System.IO.Path.Combine("logs", "ai_selftest.txt")) { AutoFlush = true },
                phaseStart = now,
            };
            selfTest.log.WriteLine($"creature AI self-test started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        SelfTest t = selfTest;
        try {
            runSelfTest(t, now);
        } catch (Exception e) {
            t.log.WriteLine($"SELFTEST EXCEPTION: {e}");
            finishSelfTest(t);
        }
    }

    private void runSelfTest(SelfTest t, double now) {
        double elapsed = now - t.phaseStart;
        switch (t.phase) {
            case 0: {
                    // 1) every loot item must be creatable from the DAT entity definitions
                    int lootOk = 0;
                    foreach (uint did in LOOT_DIDS) {
                        try {
                            WorldObject probe = world.objectManager.create(InstanceId.IdType.Dynamic, new DataId(did), DataId.NULL, false);
                            t.log.WriteLine($"loot 0x{did:X8}: ok  name='{displayName(probe, "(no name)")}' package={probe.packageType} icon=0x{probe.iconDid.id:X8} visual=0x{probe.physicsEntityDid.id:X8}");
                            probe.delete();
                            lootOk++;
                        } catch (Exception e) {
                            t.log.WriteLine($"loot 0x{did:X8}: FAILED {e.GetType().Name}: {e.Message}");
                        }
                    }
                    t.log.WriteLine($"loot items creatable: {lootOk}/{LOOT_DIDS.Length}");

                    // 2) cell numbering and landblock crossing against positions seen in the capture
                    checkCell(t, 0x77, 0xBA, 145.32103f, 102.15993f, 0x3E);
                    checkCell(t, 0x77, 0xBA, 131.90598f, 101.792984f, 0x36);
                    checkCell(t, 0x77, 0xBA, 142.78621f, 79.9371f, 0x3C);
                    checkCell(t, 0x75, 0xB9, 131.13126f, 13.535009f, 0x31);
                    PositionOffset crossed = moved(new CellId(0x77, 0xBA, 0, 0x3E), new Vector3(158.0f, 80.0f, 5.0f), new Vector3(5.0f, 0.0f, 0.0f));
                    t.log.WriteLine($"crossing east from <77 BA> x=158 by 5 m -> {crossed.cell} {crossed.offset}  (expected <78 BA> x=3)");
                    Position pa = new() { cell = new CellId(0x77, 0xBA, 0, 0x3E), frame = new Frame(new Vector3(158.0f, 80.0f, 5.0f), Quaternion.Identity) };
                    Position pb = new() { cell = new CellId(0x78, 0xBA, 0, 0x04), frame = new Frame(new Vector3(3.0f, 80.0f, 6.0f), Quaternion.Identity) };
                    t.log.WriteLine($"distance across the border: {(tryDelta(pa, pb, out Vector3 across) ? across.ToString() : "unreachable")}  (expected <5, 0, 1>)");

                    // 3) load landblocks so creatures register, pick one outdoor creature
                    foreach ((byte x, byte y) in new (byte, byte)[] { (0x75, 0xB9), (0x74, 0xB9), (0x76, 0xB9), (0x75, 0xBA), (0x75, 0xB8), (0x77, 0xBA), (0x7B, 0xB9) }) {
                        world.landblockManager.get(new LandblockId(x, y));
                    }
                    t.log.WriteLine($"creatures registered: {monsters.Count} (outdoor: {monsters.Values.Count(v => v.canMove)})");

                    Monster? pick = monsters.Values.Where(v => v.canMove && v.state == State.Idle && v.obj.inWorld).OrderBy(v => v.obj.id.id).FirstOrDefault();
                    if (pick == null) {
                        t.log.WriteLine("no outdoor creature found - stopping");
                        finishSelfTest(t);
                        return;
                    }
                    t.m = pick;
                    t.startPos = pick.home.frame.pos;
                    t.log.WriteLine($"creature: id={pick.obj.id} name='{displayName(pick.obj, "?")}' home={pick.home.cell} {pick.home.frame.pos} health={pick.obj.health}/{pick.obj.healthMax}");

                    // 4) stand-in for a player: a plain object 8 m east of the creature's home
                    WorldObject dummy = world.objectManager.create(InstanceId.IdType.Dynamic, new DataId(LOOT_DIDS[0]), DataId.NULL, false);
                    dummy.healthMax = 100;
                    dummy.health = 100;
                    placeDummy(t, dummy, 8.0f);
                    t.dummy = dummy;
                    t.lastDummyHealth = 100;

                    bodyRemoveDelay = 3.0;
                    respawnDelay = 8.0;
                    aggro(pick, dummy, now);
                    t.phase = 1;
                    t.phaseStart = now;
                    break;
                }
            case 1: {
                    selfTestLog(t, now, elapsed);
                    if (tryDelta(t.m.obj.pos, t.dummy.pos, out Vector3 d)) {
                        t.closestApproach = MathF.Min(t.closestApproach, horizontal(d));
                    }
                    if (t.dummy.health < t.lastDummyHealth) {
                        t.hitsSeen++;
                        t.log.WriteLine($"  +{elapsed:F1}s the stand-in lost {t.lastDummyHealth - t.dummy.health} health (now {t.dummy.health}/100)");
                        t.lastDummyHealth = t.dummy.health;
                    }
                    if (t.hitsSeen >= 2 || elapsed > 30.0) {
                        t.log.WriteLine($"fight phase over after {elapsed:F1}s: hits landed={t.hitsSeen}, closest approach={t.closestApproach:F1} m, creature state={t.m.state}");
                        placeDummy(t, t.dummy, 45.0f); // lure it away: it should give up and run home
                        t.log.WriteLine("stand-in moved 45 m from the creature's home: the creature should give up and return");
                        t.phase = 2;
                        t.phaseStart = now;
                    }
                    break;
                }
            case 2: {
                    selfTestLog(t, now, elapsed);
                    if (t.m.state == State.Idle || elapsed > 30.0) {
                        bool atHome = tryDelta(t.m.obj.pos, t.m.home, out Vector3 d) && horizontal(d) < 1.5f;
                        t.log.WriteLine($"leash phase over after {elapsed:F1}s: state={t.m.state}, back at home={atHome}, health={t.m.obj.health}/{t.m.obj.healthMax}, mode={t.m.obj.mode}");

                        t.log.WriteLine("killing the creature now");
                        t.m.obj.applyDamage(t.m.obj.healthMax, t.dummy);
                        t.log.WriteLine($"creature state={t.m.state} dead={t.m.obj.dead} selectable={t.m.obj.selectable}; loot on the ground: {groundLoot.Count}");
                        foreach ((WorldObject item, double expires) in groundLoot) {
                            t.log.WriteLine($"  loot: id={item.id} name='{displayName(item, "?")}' at {item.pos.cell} {item.pos.frame.pos} inWorld={item.inWorld} takeable={item.takeable} persistent={item.persistent}");
                        }
                        t.phase = 3;
                        t.phaseStart = now;
                    }
                    break;
                }
            case 3: {
                    if (now >= t.nextLog) {
                        t.nextLog = now + 1.0;
                        t.log.WriteLine($"  +{elapsed:F1}s state={t.m.state} inWorld={t.m.obj.inWorld} dead={t.m.obj.dead} health={t.m.obj.health}");
                    }
                    if (t.m.state == State.Idle && !t.m.obj.dead && t.m.obj.inWorld) {
                        t.respawned = true;
                        t.log.WriteLine($"respawned after {elapsed:F1}s: health={t.m.obj.health}/{t.m.obj.healthMax} at {t.m.obj.pos.cell} {t.m.obj.pos.frame.pos}");
                        finishSelfTest(t);
                    } else if (elapsed > 30.0) {
                        t.log.WriteLine("the creature did NOT respawn within 30 s");
                        finishSelfTest(t);
                    }
                    break;
                }
        }
    }

    private void checkCell(SelfTest t, byte lbX, byte lbY, float x, float y, int expected) {
        PositionOffset p = moved(new CellId(lbX, lbY, 0, 1), new Vector3(x, y, 0.0f), Vector3.Zero);
        t.log.WriteLine($"cell check ({x:F2}, {y:F2}) -> {p.cell}  expected outdoor cell 0x{expected:X2}: {(p.cell.outdoorCellId == expected ? "OK" : "WRONG")}");
    }

    private void placeDummy(SelfTest t, WorldObject dummy, float metresEast) {
        PositionOffset spot = moved(t.m.home.cell, t.m.home.frame.pos, new Vector3(metresEast, 0.0f, 0.0f));
        dummy.pos = new Position { cell = spot.cell, frame = new Frame(spot.offset, Quaternion.Identity) };
        dummy.enterWorld();
        t.log.WriteLine($"stand-in target placed at {spot.cell} {spot.offset} ({metresEast} m east of the creature's home)");
    }

    private void selfTestLog(SelfTest t, double now, double elapsed) {
        if (now < t.nextLog) {
            return;
        }
        t.nextLog = now + 1.0;
        string distance = tryDelta(t.m.obj.pos, t.dummy.pos, out Vector3 d) ? horizontal(d).ToString("F1") : "?";
        t.log.WriteLine($"  +{elapsed:F1}s state={t.m.state} moving={t.m.moving} at {t.m.obj.pos.cell} ({t.m.obj.pos.frame.pos.X:F1}, {t.m.obj.pos.frame.pos.Y:F1}, {t.m.obj.pos.frame.pos.Z:F1}) heading={t.m.obj.heading:F0} distance-to-stand-in={distance} mode={t.m.obj.mode}");
    }

    private void finishSelfTest(SelfTest t) {
        try {
            t.log.WriteLine($"SELFTEST SUMMARY: hits landed on the stand-in={t.hitsSeen}, closest approach={t.closestApproach:F1} m, respawned={t.respawned}");
            if (t.dummy != null) {
                t.dummy.delete();
            }
            foreach ((WorldObject item, double expires) in groundLoot) {
                item.delete();
            }
            groundLoot.Clear();
            t.log.WriteLine("self-test finished; test objects removed");
            t.log.Dispose();
        } finally {
            bodyRemoveDelay = 25.0;
            respawnDelay = 60.0;
            selfTest = null;
            selfTestDone = true;
        }
    }
}

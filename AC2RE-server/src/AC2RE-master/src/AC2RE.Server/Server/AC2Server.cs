using AC2RE.Definitions;
using AC2RE.Server.Database;
using AC2RE.Server.Migration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace AC2RE.Server;

internal class AC2Server {

    private static readonly double TICK_DELTA_TIME = 1.0 / 20.0;
    private static readonly double MAX_DELTA_TIME = TICK_DELTA_TIME * 3.0;

    private readonly ServerTime time = new(TICK_DELTA_TIME, MAX_DELTA_TIME);

    private bool active;

    private IMigrationDatabase? migrationDb;
    private IAccountDatabase? accountDb;
    private IMapDatabase? mapDb;
    private IWorldDatabase? worldDb;

    private MigrationManager? migrationManager;

    private AccountManager? accountManager;
    private ClientManager? clientManager;
    private ContentManager? contentManager;
    private PlayerManager? playerManager;

    private World? world;

    private PacketReceiveManager? packetReceiveManager;

    private NetInterface? logonNetInterface;
    private NetInterface? gameNetInterface;
    private readonly List<ServerListener> serverListeners = new();

    [MethodImpl(MethodImplOptions.Synchronized)]
    ~AC2Server() {
        if (active) {
            Logs.STATUS.warn("Didn't stop AC2Server before destruction!");
            stop();
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void start(int port = 0) {
        if (active) {
            stop();
        }

        time.restart();

        Logs.STATUS.info("Connecting to database...");
        while (true) {
            try {
                BaseMySqlDatabase.createConnection().Dispose();
                break;
            } catch (Exception e) {
                Logs.STATUS.error(e, "Failed to make initial database connection");
            }
        }

        Logs.STATUS.info("Running migrations...");
        MigrationManager.bootstrap();
        migrationDb = new MigrationMySqlDatabase();
        migrationManager = new(migrationDb);
        migrationManager.runMigrations();

        Logs.STATUS.info("Initializing databases...");
        accountDb = new AccountMySqlDatabase();
        mapDb = new MapMySqlDatabase();
        worldDb = new WorldMySqlDatabase();

        Logs.STATUS.info("Initializing account manager...");
        accountManager = new(accountDb);

        Logs.STATUS.info("Initializing client manager...");
        clientManager = new();

        Logs.STATUS.info("Initializing content manager...");
        contentManager = new();

        // MOD2: self-check - how many map entries can this client/DAT version actually build?
        try {
            List<uint> mapDids = mapDb.getDistinctEntityDids();
            int unusable = 0;
            foreach (uint did in mapDids) {
                try {
                    EntityDef def = contentManager.getInheritedEntityDef(new(did));
                    if (def.type != EntityType.Weenie && def.type != EntityType.EntityDesc) {
                        unusable++;
                    }
                } catch (Exception) {
                    unusable++;
                }
            }
            Logs.STATUS.info("Map object self-check", "distinctEntityDefs", mapDids.Count, "unusable", unusable);

            // MOD2 diagnostics: what does each map entity DO (usage action, portal destination)? -> logs\map_entities.txt
            List<string> lines = new();
            foreach (uint did in mapDids) {
                try {
                    EntityDef def = contentManager.getInheritedEntityDef(new(did));
                    string usageState = "-";
                    try {
                        WState ws = contentManager.getWeenieStateFromEntityDid(new(did));
                        if (ws.package is gmEntity ge) {
                            DataId ua = ge.usageAction?.wstateDid ?? DataId.NULL;
                            if (ua != DataId.NULL) {
                                usageState = contentManager.getWeenieState(ua).packageType.ToString();
                            }
                        }
                    } catch (Exception) {
                        usageState = "?";
                    }
                    string dest = def.strings.GetValueOrDefault(PropertyName.PortalDestination) ?? "-";
                    lines.Add($"{did}\t{def.type}\t{def.packageType}\tusage={def.usageAction}\tusageState={usageState}\tdest={dest}\tints={def.ints.Count}\tstrings={def.strings.Count}");
                } catch (Exception e) {
                    lines.Add($"{did}\tERROR {e.GetType().Name}: {e.Message}");
                }
            }
            File.WriteAllLines(Path.Combine("logs", "map_entities.txt"), lines);
        } catch (Exception e) {
            Logs.STATUS.warn(e, "Map object self-check failed");
        }

        PortalTable.load();

        Logs.STATUS.info("Initializing player manager...");
        playerManager = new(clientManager);

        Logs.STATUS.info("Initializing world...");
        world = new(mapDb, worldDb, time, contentManager, playerManager);

        Logs.STATUS.info("Initializing packet receive manager...");
        packetReceiveManager = new(accountManager, clientManager, time, world);

        Logs.STATUS.info("Initializing network...");
        logonNetInterface = new(port);
        gameNetInterface = new(logonNetInterface.port + 1);
        serverListeners.Add(new(logonNetInterface, packetReceiveManager.processReceive));
        serverListeners.Add(new(gameNetInterface, packetReceiveManager.processReceive));

        Logs.STATUS.info("Server initialization complete.");

        active = true;
    }

    /// <summary>MOD2: save characters and world objects to the database without stopping the server.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void saveWorld() {
        if (!active) {
            return;
        }

        world!.save();
        time.persist();
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void stop() {
        if (!active) {
            return;
        }

        world!.save();
        time.persist();

        world!.disconnectAll();

        clientManager!.processClients(client => client.flushSend(gameNetInterface!, time.time, time.elapsedTime));

        foreach (ServerListener serverListener in serverListeners) {
            serverListener.stop();
        }
        serverListeners.Clear();

        logonNetInterface!.close();
        gameNetInterface!.close();

        contentManager!.Dispose();

        migrationManager = null;

        accountManager = null;
        clientManager = null;
        contentManager = null;
        playerManager = null;

        world = null;

        packetReceiveManager = null;

        migrationDb!.Dispose();
        accountDb!.Dispose();
        mapDb!.Dispose();
        worldDb!.Dispose();

        migrationDb = null;
        accountDb = null;
        mapDb = null;
        worldDb = null;

        active = false;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void tick() {
        if (!active) {
            return;
        }

        time.beginFrame();

        while (time.tryTick()) {
            packetReceiveManager!.processNetBlobs();

            world!.tick();

            clientManager!.processClients(client => client.flushSend(gameNetInterface!, time.time, time.elapsedTime));
        }
    }
}

using System;
using System.IO;
using System.Threading;

namespace AC2RE.Server;

internal static class Program {

    private static readonly int SERVER_LOGON_PORT = 7777;
    private static readonly AC2Server SERVER = new();

    // MOD2 additions: periodic autosave, and a "stop file" so a launcher can shut the server down cleanly (with a
    // final save) even when it has no console window to send keys or Ctrl+C to.
    private static readonly TimeSpan AUTOSAVE_INTERVAL = TimeSpan.FromMinutes(2);
    private const string STOP_FILE = "stop.flag";

    private static void Main(string[] args) {
        Logs.STATUS.info("Hello World!");

        runServer();
    }

    private static bool keyAvailable() {
        try {
            return Console.KeyAvailable;
        } catch (InvalidOperationException) {
            return false;   // no console attached or input redirected
        }
    }

    private static void runServer() {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SERVER.stop();
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            SERVER.stop();      // saves the world
            Environment.Exit(0);
        };

        SERVER.start(SERVER_LOGON_PORT);
        DateTime nextAutosave = DateTime.UtcNow + AUTOSAVE_INTERVAL;

        while (true) {
            if (File.Exists(STOP_FILE)) {
                Logs.STATUS.info("Stop requested: saving the world and shutting down.");
                try {
                    File.Delete(STOP_FILE);
                } catch (IOException) {
                }
                SERVER.stop();
                Environment.Exit(0);
            }

            if (keyAvailable()) {
                switch (Console.ReadKey(true).Key) {
                    case ConsoleKey.Enter:
                        SERVER.start(SERVER_LOGON_PORT);
                        break;
                    case ConsoleKey.Escape:
                        SERVER.stop();
                        break;
                }
            }

            if (DateTime.UtcNow >= nextAutosave) {
                nextAutosave = DateTime.UtcNow + AUTOSAVE_INTERVAL;
                try {
                    SERVER.saveWorld();
                    Logs.STATUS.info("Autosaved the world.");
                } catch (Exception e) {
                    Logs.STATUS.error(e, "Autosave failed");
                }
            }

            SERVER.tick();
            Thread.Sleep(10);
        }
    }
}

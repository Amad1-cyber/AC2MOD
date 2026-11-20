using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AC2RE.Server;

internal class ServerTime {

    // MOD2 FIX: the original clock was "seconds since 2020-01-01" (about 2.17e8 in Nov 2026). The retail client turns
    // (time - animationStart) * framesPerSecond into a 32-bit int, which overflows past ~2.147e8 / fps seconds and then
    // indexes an array with a huge negative number -> access violation right after login (AC2client.exe+0x1aedaa).
    // The real server's clock was ~4.3e6. So the clock is now a small, persistent "server uptime" counter: it only
    // advances while the server runs, is saved with every world save, and continues from there on the next start.
    private const string TIME_FILE = "servertime.dat";
    private const double DEFAULT_START_TIME = 100000.0;

    public readonly double tickDeltaTime;
    public readonly double maxDeltaTime;

    public double startTime { get; private set; }
    public double time { get; private set; }
    public float elapsedTime { get; private set; }

    private readonly Stopwatch stopwatch = new();

    private uint tick;
    private double frameAccum;
    private double frameTime;
    private double lastFrameTime;

    public ServerTime(double tickDeltaTime, double maxDeltaTime) {
        this.tickDeltaTime = tickDeltaTime;
        this.maxDeltaTime = maxDeltaTime;
    }

    public void restart() {
        if (!Stopwatch.IsHighResolution) {
            Logs.GENERAL.warn("High resolution stopwatch is not available");
        }

        startTime = loadStartTime();
        time = startTime;
        elapsedTime = 0.0f;

        stopwatch.Restart();

        tick = 0;
        frameAccum = 0.0;
        frameTime = 0.0;
        lastFrameTime = 0.0;
    }

    private static double loadStartTime() {
        try {
            if (File.Exists(TIME_FILE)
                && double.TryParse(File.ReadAllText(TIME_FILE).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double saved)
                && saved >= DEFAULT_START_TIME && saved < 1.0e8) {
                return saved;
            }
        } catch (IOException) {
        }
        return DEFAULT_START_TIME;
    }

    /// <summary>MOD2: remember the current server time so the next start continues from it.</summary>
    public void persist() {
        try {
            File.WriteAllText(TIME_FILE, time.ToString("R", CultureInfo.InvariantCulture));
        } catch (IOException) {
        }
    }

    internal void beginFrame() {
        frameTime = stopwatch.Elapsed.TotalSeconds;
        frameAccum += frameTime - lastFrameTime;
        lastFrameTime = frameTime;
    }

    internal bool tryTick() {
        if (stopwatch.Elapsed.TotalSeconds - frameTime > maxDeltaTime) {
            // Ran overtime - reset accumulator to try and catch up, and stop ticking which effectively slows down the simulation
            frameAccum = 0.0f;
            return false;
        }

        if (frameAccum < tickDeltaTime) {
            // Not enough time remaining in accumulator for a full tick, so stop ticking
            return false;
        }

        frameAccum -= tickDeltaTime;

        tick++;
        double elapsedTimeDouble = tick * tickDeltaTime;
        elapsedTime = (float)elapsedTimeDouble;
        time = startTime + elapsedTimeDouble;

        return true;
    }
}

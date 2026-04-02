using BuildVersionBot.Core;
using BuildVersionBot.Data;
using BuildVersionBot.Networking;
using BuildVersionBot.Security;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace BuildVersionBot;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        FileLogger? log = null;

        try
        {
            var cfg = LoadConfig();
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "LOG"));
            log = new FileLogger(Path.Combine(AppContext.BaseDirectory, "LOG"));

            bool verboseMode = args.Length >= 1 && string.Equals(args[0], "-verbose", StringComparison.OrdinalIgnoreCase);
            string? computerListPath = verboseMode && args.Length >= 2 ? args[1] : null;

            log.Info($"START BOT (build version scanner), verbose={verboseMode}");
            Console.WriteLine(verboseMode
                ? "== BuildVersionBot - tryb verbose =="
                : "== BuildVersionBot - tryb nienadzorowany ==");

            while (!cts.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var endTimeToday = now.Date.AddHours(cfg.Runner.END_HOUR);

                if (now >= endTimeToday)
                {
                    log.Info($"END_HOUR reached ({cfg.Runner.END_HOUR}:00). Bot stops.");
                    Console.WriteLine($"[INFO] Osiągnięto END_HOUR={cfg.Runner.END_HOUR}:00. Koniec pracy.");
                    break;
                }

                try
                {
                    await RunSingleCycleAsync(cfg, args, verboseMode, computerListPath, log, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    log.Info("Cycle cancelled by user.");
                    break;
                }
                catch (Exception ex)
                {
                    log.Error($"CRITICAL cycle error: {ex}");
                    Console.Error.WriteLine($"[CRITICAL] {ex.Message}");
                }

                if (verboseMode)
                {
                    log.Info("Verbose mode finished after single cycle.");
                    break;
                }

                now = DateTime.Now;
                endTimeToday = now.Date.AddHours(cfg.Runner.END_HOUR);

                if (now >= endTimeToday)
                {
                    log.Info($"END_HOUR reached after cycle ({cfg.Runner.END_HOUR}:00). Bot stops.");
                    Console.WriteLine($"[INFO] Osiągnięto END_HOUR={cfg.Runner.END_HOUR}:00 po cyklu. Koniec pracy.");
                    break;
                }

                log.Info($"Sleep for {cfg.Runner.DELAY} minute(s) before next cycle.");
                Console.WriteLine($"[INFO] Przerwa {cfg.Runner.DELAY} min...");
                await Task.Delay(TimeSpan.FromMinutes(cfg.Runner.DELAY), cts.Token);
            }

            log.Info("END BOT");
            return 0;
        }
        catch (OperationCanceledException)
        {
            log?.Info("BOT cancelled by user.");
            return 130;
        }
        catch (Exception ex)
        {
            log?.Error($"CRITICAL startup error: {ex}");
            Console.Error.WriteLine($"Błąd krytyczny startu: {ex.Message}");
            return 3;
        }
    }

    private static async Task RunSingleCycleAsync(AppConfig cfg, string[] args, bool verboseMode, string? computerListPath, FileLogger log, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine($"[INFO] Start cyklu: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine("==================================================");
        log.Info($"=== START CYCLE === verbose={verboseMode}");

        string secureFile = cfg.Database.SecureConnFile;
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[^1]) && !args[^1].Equals("-verbose", StringComparison.OrdinalIgnoreCase) && File.Exists(args[^1]) && args[^1].EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            secureFile = args[^1];

        if (!File.Exists(secureFile))
        {
            log.Error($"Brak pliku z connection stringiem: {secureFile}");
            Console.Error.WriteLine($"Brak pliku z connection stringiem: {secureFile}");
            return;
        }

        IConnectionStringProvider provider = new AesConnectionStringProvider(secureFile);
        string connString = provider.GetConnectionString();

        var csb = new SqlConnectionStringBuilder(connString);
        if (!string.IsNullOrWhiteSpace(cfg.Database.OverrideServer))
            csb.DataSource = cfg.Database.OverrideServer;
        if (cfg.Database.ForceEncrypt.HasValue)
            csb.Encrypt = cfg.Database.ForceEncrypt.Value;
        if (cfg.Database.ForceTrustServerCertificate.HasValue)
            csb.TrustServerCertificate = cfg.Database.ForceTrustServerCertificate.Value;

        connString = csb.ConnectionString;
        log.Info($"Conn: {csb.DataSource}; DB: {csb.InitialCatalog}; Table: {cfg.Table.Schema}.{cfg.Table.Name}");

        var repo = new BuildScanRepository(
            connString,
            cfg.Table.Schema,
            cfg.Table.Name,
            cfg.Table.ComputerNameColumn,
            cfg.Table.DescriptionColumn,
            cfg.Table.LastScanColumn,
            cfg.Table.OperatorColumn,
            cfg.Table.ResultColumn,
            cfg.Table.TargetDescriptionValue,
            cfg.Table.DoneDescriptionValue,
            cfg.Table.OfflineResultValue,
            cfg.Database.CommandTimeoutSeconds);

        List<BuildScanTarget> targets;
        if (verboseMode)
        {
            if (string.IsNullOrWhiteSpace(computerListPath) || !File.Exists(computerListPath))
                throw new FileNotFoundException("W trybie -verbose podaj ścieżkę do pliku computers.txt", computerListPath);

            var computers = File.ReadAllLines(computerListPath)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("#"))
                .ToList();

            targets = repo.GetTargetsFromList(computers);
            log.Info($"Verbose mode: targets from file '{computerListPath}': {targets.Count}");
        }
        else
        {
            targets = await repo.GetTargetsAsync(cfg.Runner.PERIOD, ct);
            log.Info($"Targets loaded from DB: {targets.Count}");
        }

        Console.WriteLine($"[INFO] Rekordy do sprawdzenia: {targets.Count}");

        INetworkDiagnosticService net = new NetworkDiagnosticService();
        var semaphore = new SemaphoreSlim(cfg.Runner.MAX_PARALLEL, cfg.Runner.MAX_PARALLEL);

        foreach (var batch in targets.Chunk(cfg.Runner.BATCH_SIZE))
        {
            ct.ThrowIfCancellationRequested();

            var names = batch.Select(x => x.ComputerName).ToList();
            var stillEligible = await repo.GetStillEligibleAsync(names, verboseMode, ct);
            var toProcess = batch.Where(x => stillEligible.Contains(x.ComputerName)).ToList();

            var tasks = toProcess.Select(async target =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    string computer = target.ComputerName;
                    DateTime scanTime = DateTime.Now;

                    var diag = await net.IsHostActiveAsync(computer);
                    if (!diag.IsActive)
                    {
                        return new BuildScanUpdate(
                            computer,
                            scanTime,
                            cfg.Table.BotOperatorValue,
                            cfg.Table.OfflineResultValue,
                            false);
                    }

                    string result = RemoteBuildVersionReader.ReadBuildVersion(computer);
                    bool markDone = string.Equals(result, "Windows 11, 24h2", StringComparison.OrdinalIgnoreCase);

                    return new BuildScanUpdate(
                        computer,
                        scanTime,
                        cfg.Table.BotOperatorValue,
                        string.IsNullOrWhiteSpace(result) ? cfg.Table.ErrorResultValue : result,
                        markDone);
                }
                catch
                {
                    return new BuildScanUpdate(
                        target.ComputerName,
                        DateTime.Now,
                        cfg.Table.BotOperatorValue,
                        cfg.Table.ErrorResultValue,
                        false);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            var batchResult = await repo.UpdateBatchAsync(results.ToList(), verboseMode, ct);

            foreach (var u in batchResult.UpdatedItems)
            {
                Console.WriteLine($"[OK] {u.ComputerName} => {u.ResultValue}");
                log.Info($"DB UPDATE {u.ComputerName}: LAST_SCAN={u.ScanTime:yyyy-MM-dd HH:mm:ss}, OPERATOR={u.OperatorName}, RESULT={u.ResultValue}, DONE={u.MarkDone}");
            }

            foreach (var s in batchResult.SkippedItems)
            {
                Console.WriteLine($"[SKIP] {s} -> rekord zmieniony ręcznie w międzyczasie");
                log.Info($"SKIP {s} changed manually in the meantime");
            }

            foreach (var f in batchResult.FailedItems)
            {
                Console.WriteLine($"[ERROR] {f.ComputerName} -> {f.Error}");
                log.Error($"ERROR {f.ComputerName}: {f.Error}");
            }
        }

        log.Info("=== END CYCLE ===");
    }

    private static AppConfig LoadConfig()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
            path = "appsettings.json";

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (cfg is null)
            throw new InvalidOperationException("Nie udało się wczytać appsettings.json.");

        return cfg;
    }
}

internal sealed class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public TableConfig Table { get; set; } = new();
    public RunnerConfig Runner { get; set; } = new();

    internal sealed class DatabaseConfig
    {
        public string SecureConnFile { get; set; } = "secureconn.dat";
        public int CommandTimeoutSeconds { get; set; } = 60;
        public string? OverrideServer { get; set; } = null;
        public bool? ForceEncrypt { get; set; } = true;
        public bool? ForceTrustServerCertificate { get; set; } = true;
    }

    internal sealed class TableConfig
    {
        public string Schema { get; set; } = "dbo";
        public string Name { get; set; } = "OHD_24h2";
        public string ComputerNameColumn { get; set; } = "COMPUTER_NAME";
        public string DescriptionColumn { get; set; } = "DESCRIPTION";
        public string LastScanColumn { get; set; } = "LAST_SCAN";
        public string OperatorColumn { get; set; } = "OPERATOR";
        public string ResultColumn { get; set; } = "RESULT";

        public string TargetDescriptionValue { get; set; } = "Do realizacji";
        public string DoneDescriptionValue { get; set; } = "Zrobione";
        public string OfflineResultValue { get; set; } = "OFFLINE";
        public string ErrorResultValue { get; set; } = "BŁĄD";
        public string BotOperatorValue { get; set; } = "Hades2BotBuildVersion";
    }

    internal sealed class RunnerConfig
    {
        public int END_HOUR { get; set; } = 18;
        public int DELAY { get; set; } = 30;
        public int PERIOD { get; set; } = 8;
        public int BATCH_SIZE { get; set; } = 5;
        public int MAX_PARALLEL { get; set; } = 5;
    }
}

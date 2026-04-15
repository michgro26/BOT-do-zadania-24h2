using BuildVersionBot.Core;
using BuildVersionBot.Data;
using BuildVersionBot.Networking;
using BuildVersionBot.Security;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BuildVersionBot
{
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

                bool isVerbose = TryGetVerboseFile(args, out string? verboseFileArg);
                string? verboseFilePath = isVerbose ? ResolveVerboseFilePath(verboseFileArg!) : null;

                log.Info($"START BOT (build version scanner), verbose={isVerbose}");
                Console.WriteLine(isVerbose
                    ? "== BuildVersionBot - tryb verbose =="
                    : "== BuildVersionBot - tryb nienadzorowany ==");

                if (isVerbose)
                {
                    try
                    {
                        await RunSingleCycleAsync(cfg, args, log, cts.Token, true, verboseFilePath);
                        log.Info("END BOT (verbose)");
                        return 0;
                    }
                    catch (OperationCanceledException)
                    {
                        log.Info("BOT cancelled by user (verbose).");
                        return 130;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"CRITICAL verbose error: {ex}");
                        Console.Error.WriteLine($"[CRITICAL] {ex.Message}");
                        return 3;
                    }
                }

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
                        await RunSingleCycleAsync(cfg, args, log, cts.Token, false, null);
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

        private static async Task RunSingleCycleAsync(
            AppConfig cfg,
            string[] args,
            FileLogger log,
            CancellationToken ct,
            bool isVerbose,
            string? verboseFilePath)
        {
            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine($"[INFO] Start cyklu: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine("==================================================");

            log.Info($"=== START CYCLE === verbose={isVerbose}");

            string secureFile = cfg.Database.SecureConnFile;
            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) && !string.Equals(args[0], "-verbose", StringComparison.OrdinalIgnoreCase))
                secureFile = args[0];

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

            if (isVerbose)
            {
                if (string.IsNullOrWhiteSpace(verboseFilePath))
                    throw new InvalidOperationException("Brak ścieżki do pliku computers.txt dla trybu verbose.");

                targets = LoadTargetsFromFile(verboseFilePath);
                Console.WriteLine($"[INFO] Tryb verbose. Stacje z pliku: {targets.Count}");
                log.Info($"Verbose mode. Targets from file '{verboseFilePath}': {targets.Count}");

                var requestedNames = targets.Select(x => x.ComputerName).ToList();
                var existingInDb = await repo.GetExistingComputerNamesAsync(requestedNames, ct);

                var missingInDb = requestedNames
                    .Where(x => !existingInDb.Contains(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (missingInDb.Count > 0)
                {
                    SaveMissingInDatabaseReport(missingInDb, log);
                    Console.WriteLine($"[INFO] Brak w bazie: {missingInDb.Count} (zapisano do BRAK_W_BAZIE.txt)");
                    log.Info($"Missing in DB: {missingInDb.Count}. Saved to BRAK_W_BAZIE.txt");
                }

                targets = targets
                    .Where(x => existingInDb.Contains(x.ComputerName))
                    .ToList();

                Console.WriteLine($"[INFO] Tryb verbose. Stacje istniejące w bazie: {targets.Count}");
                log.Info($"Verbose mode. Targets existing in DB: {targets.Count}");
            }
            else
            {
                targets = await repo.GetTargetsAsync(cfg.Runner.PERIOD, ct);
                Console.WriteLine($"[INFO] Rekordy do sprawdzenia: {targets.Count}");
                log.Info($"Targets loaded from DB: {targets.Count}");
            }

            if (targets.Count == 0)
            {
                log.Info("Brak rekordów do sprawdzenia.");
                Console.WriteLine("[INFO] Brak rekordów do sprawdzenia.");
                log.Info("=== END CYCLE ===");
                return;
            }

            INetworkDiagnosticService net = new NetworkDiagnosticService();
            var semaphore = new SemaphoreSlim(cfg.Runner.MAX_PARALLEL, cfg.Runner.MAX_PARALLEL);

            foreach (var batch in targets.Chunk(cfg.Runner.BATCH_SIZE))
            {
                ct.ThrowIfCancellationRequested();

                List<BuildScanTarget> toProcess;

                if (isVerbose)
                {
                    var names = batch.Select(x => x.ComputerName).ToList();
                    var stillEligible = await repo.GetStillEligibleAsync(names, ct);

                    toProcess = batch
                        .Where(x => stillEligible.Contains(x.ComputerName))
                        .ToList();

                    foreach (var skipped in batch.Where(x => !stillEligible.Contains(x.ComputerName)))
                    {
                        Console.WriteLine($"[SKIP] {skipped.ComputerName} -> rekord nie jest już 'Do realizacji'.");
                        log.Info($"SKIP {skipped.ComputerName} -> not eligible anymore (DESCRIPTION != '{cfg.Table.TargetDescriptionValue}')");
                    }
                }
                else
                {
                    var names = batch.Select(x => x.ComputerName).ToList();
                    var stillEligible = await repo.GetStillEligibleAsync(names, ct);

                    toProcess = batch
                        .Where(x => stillEligible.Contains(x.ComputerName))
                        .ToList();

                    foreach (var skipped in batch.Where(x => !stillEligible.Contains(x.ComputerName)))
                    {
                        Console.WriteLine($"[SKIP] {skipped.ComputerName} -> rekord zmieniony ręcznie w międzyczasie.");
                        log.Info($"SKIP {skipped.ComputerName} -> changed manually in the meantime");
                    }
                }

                if (toProcess.Count == 0)
                    continue;

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
                        if (string.IsNullOrWhiteSpace(result))
                            result = cfg.Table.ErrorResultValue;

                        bool markDone = string.Equals(
                            result,
                            cfg.Table.ExpectedSuccessResultValue,
                            StringComparison.OrdinalIgnoreCase);

                        return new BuildScanUpdate(
                            computer,
                            scanTime,
                            cfg.Table.BotOperatorValue,
                            result,
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
                var batchResult = await repo.UpdateBatchAsync(results.ToList(), ct);

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

                if (isVerbose && !string.IsNullOrWhiteSpace(verboseFilePath))
                {
                    var successfulToRemove = batchResult.UpdatedItems
                        .Where(x => string.Equals(
                            x.ResultValue,
                            cfg.Table.ExpectedSuccessResultValue,
                            StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.ComputerName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (successfulToRemove.Count > 0)
                    {
                        RemoveProcessedComputersFromFile(verboseFilePath, successfulToRemove, log);
                    }
                }
            }

            log.Info("=== END CYCLE ===");
        }

        private static bool TryGetVerboseFile(string[] args, out string? verboseFile)
        {
            verboseFile = null;

            if (args is null || args.Length < 2)
                return false;

            if (!string.Equals(args[0], "-verbose", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.IsNullOrWhiteSpace(args[1]))
                return false;

            verboseFile = args[1];
            return true;
        }

        private static string ResolveVerboseFilePath(string fileArg)
        {
            if (Path.IsPathRooted(fileArg))
                return fileArg;

            string fromBaseDirectory = Path.Combine(AppContext.BaseDirectory, fileArg);
            if (File.Exists(fromBaseDirectory))
                return fromBaseDirectory;

            return Path.GetFullPath(fileArg);
        }

        private static List<BuildScanTarget> LoadTargetsFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Nie znaleziono pliku z listą komputerów: {filePath}");

            return File.ReadAllLines(filePath)
                .Select(x => x?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(x => new BuildScanTarget(x!))
                .ToList();
        }

        private static void SaveMissingInDatabaseReport(List<string> missingComputers, FileLogger log)
        {
            try
            {
                if (missingComputers == null || missingComputers.Count == 0)
                    return;

                string reportPath = Path.Combine(AppContext.BaseDirectory, "BRAK_W_BAZIE.txt");
                File.WriteAllLines(reportPath, missingComputers.OrderBy(x => x));

                log.Info($"Saved missing-in-database report: {reportPath}. Count={missingComputers.Count}");
            }
            catch (Exception ex)
            {
                log.Error($"Nie udało się zapisać raportu BRAK_W_BAZIE.txt: {ex}");
            }
        }

        private static void RemoveProcessedComputersFromFile(string filePath, List<string> processedComputers, FileLogger log)
        {
            try
            {
                if (processedComputers.Count == 0)
                    return;

                if (!File.Exists(filePath))
                    return;

                var removeSet = new HashSet<string>(processedComputers, StringComparer.OrdinalIgnoreCase);

                var originalLines = File.ReadAllLines(filePath).ToList();
                var remainingLines = originalLines
                    .Where(line => !removeSet.Contains((line ?? string.Empty).Trim()))
                    .ToList();

                File.WriteAllLines(filePath, remainingLines);

                log.Info($"Verbose file updated: removed {removeSet.Count} computer(s) from '{filePath}'. Removed: {string.Join(", ", removeSet)}");
            }
            catch (Exception ex)
            {
                log.Error($"Nie udało się zaktualizować pliku verbose '{filePath}': {ex}");
            }
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
            public string ExpectedSuccessResultValue { get; set; } = "Windows 11, 24h2";
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
}
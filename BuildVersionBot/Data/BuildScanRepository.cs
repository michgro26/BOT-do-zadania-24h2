using Microsoft.Data.SqlClient;
using System.Data;

namespace BuildVersionBot.Data;

public class BuildScanRepository
{
    private readonly string _connectionString;
    private readonly string _schema;
    private readonly string _table;
    private readonly string _colComputer;
    private readonly string _colDescription;
    private readonly string _colLastScan;
    private readonly string _colOperator;
    private readonly string _colResult;
    private readonly string _targetDescriptionValue;
    private readonly string _doneDescriptionValue;
    private readonly string _offlineResultValue;
    private readonly int _commandTimeoutSeconds;

    public BuildScanRepository(
        string connectionString,
        string schema,
        string table,
        string colComputer,
        string colDescription,
        string colLastScan,
        string colOperator,
        string colResult,
        string targetDescriptionValue,
        string doneDescriptionValue,
        string offlineResultValue,
        int commandTimeoutSeconds)
    {
        _connectionString = connectionString;
        _schema = schema;
        _table = table;
        _colComputer = colComputer;
        _colDescription = colDescription;
        _colLastScan = colLastScan;
        _colOperator = colOperator;
        _colResult = colResult;
        _targetDescriptionValue = targetDescriptionValue;
        _doneDescriptionValue = doneDescriptionValue;
        _offlineResultValue = offlineResultValue;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    private string FullTable => $"[{_schema}].[{_table}]";

    public async Task<List<BuildScanTarget>> GetTargetsAsync(int periodHours, CancellationToken ct)
    {
        string sql = $@"
WITH q AS (
    SELECT
        CAST(t.[{_colComputer}] AS nvarchar(256)) AS ComputerName,
        ROW_NUMBER() OVER (
            PARTITION BY CAST(t.[{_colComputer}] AS nvarchar(256))
            ORDER BY (SELECT 0)
        ) AS rn
    FROM {FullTable} t
    WHERE
        NULLIF(LTRIM(RTRIM(t.[{_colComputer}])), '') IS NOT NULL
        AND t.[{_colDescription}] = @targetDescription
        AND (
            t.[{_colResult}] = @offlineResult
            OR t.[{_colLastScan}] IS NULL
            OR DATEADD(HOUR, @periodHours, t.[{_colLastScan}]) <= GETDATE()
        )
)
SELECT ComputerName
FROM q
WHERE rn = 1
ORDER BY ComputerName;";

        var result = new List<BuildScanTarget>();
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@targetDescription", _targetDescriptionValue);
        cmd.Parameters.AddWithValue("@offlineResult", _offlineResultValue);
        cmd.Parameters.AddWithValue("@periodHours", periodHours);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var computer = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(computer))
                result.Add(new BuildScanTarget(computer.Trim()));
        }

        return result;
    }

    public List<BuildScanTarget> GetTargetsFromList(IEnumerable<string> computerNames)
    {
        return computerNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new BuildScanTarget(x.Trim()))
            .DistinctBy(x => x.ComputerName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.ComputerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<HashSet<string>> GetStillEligibleAsync(List<string> computerNames, bool verboseMode, CancellationToken ct)
    {
        if (computerNames.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dt = new DataTable();
        dt.Columns.Add("ComputerName", typeof(string));
        foreach (var c in computerNames.Distinct(StringComparer.OrdinalIgnoreCase))
            dt.Rows.Add(c);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();

        using (var create = new SqlCommand(@"
CREATE TABLE #names(
    ComputerName nvarchar(256) NOT NULL
);", conn, tx))
        {
            create.CommandTimeout = _commandTimeoutSeconds;
            await create.ExecuteNonQueryAsync(ct);
        }

        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx))
        {
            bulk.DestinationTableName = "#names";
            bulk.ColumnMappings.Add("ComputerName", "ComputerName");
            await bulk.WriteToServerAsync(dt, ct);
        }

        string sql = verboseMode
            ? $@"
SELECT DISTINCT CAST(t.[{_colComputer}] AS nvarchar(256)) AS ComputerName
FROM {FullTable} t
JOIN #names n
  ON CAST(t.[{_colComputer}] AS nvarchar(256)) = n.ComputerName
WHERE NULLIF(LTRIM(RTRIM(t.[{_colComputer}])), '') IS NOT NULL;"
            : $@"
SELECT DISTINCT CAST(t.[{_colComputer}] AS nvarchar(256)) AS ComputerName
FROM {FullTable} t
JOIN #names n
  ON CAST(t.[{_colComputer}] AS nvarchar(256)) = n.ComputerName
WHERE
    NULLIF(LTRIM(RTRIM(t.[{_colComputer}])), '') IS NOT NULL
    AND t.[{_colDescription}] = @targetDescription;";

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = _commandTimeoutSeconds })
        {
            if (!verboseMode)
                cmd.Parameters.AddWithValue("@targetDescription", _targetDescriptionValue);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0))
                    set.Add(reader.GetString(0).Trim());
            }
        }

        await tx.CommitAsync(ct);
        return set;
    }

    public async Task<BatchUpdateResult> UpdateBatchAsync(List<BuildScanUpdate> updates, bool verboseMode, CancellationToken ct)
    {
        updates ??= new List<BuildScanUpdate>();
        var updated = new List<BuildScanUpdate>();
        var skipped = new List<string>();
        var failed = new List<FailedItem>();

        if (updates.Count == 0)
        {
            return new BatchUpdateResult(0, 0, 0, updated, skipped, failed);
        }

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();

        try
        {
            var dt = new DataTable();
            dt.Columns.Add("ComputerName", typeof(string));
            dt.Columns.Add("ScanTime", typeof(DateTime));
            dt.Columns.Add("OperatorName", typeof(string));
            dt.Columns.Add("ResultValue", typeof(string));
            dt.Columns.Add("MarkDone", typeof(bool));

            foreach (var u in updates)
                dt.Rows.Add(u.ComputerName, u.ScanTime, u.OperatorName, u.ResultValue, u.MarkDone);

            using (var create = new SqlCommand(@"
CREATE TABLE #upd(
    ComputerName nvarchar(256) NOT NULL,
    ScanTime datetime NOT NULL,
    OperatorName nvarchar(256) NOT NULL,
    ResultValue nvarchar(256) NOT NULL,
    MarkDone bit NOT NULL
);
CREATE TABLE #done(ComputerName nvarchar(256) NOT NULL);", conn, tx))
            {
                create.CommandTimeout = _commandTimeoutSeconds;
                await create.ExecuteNonQueryAsync(ct);
            }

            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx))
            {
                bulk.DestinationTableName = "#upd";
                foreach (DataColumn c in dt.Columns)
                    bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                await bulk.WriteToServerAsync(dt, ct);
            }

            string sql = verboseMode
                ? $@"
UPDATE t
SET
    t.[{_colLastScan}] = u.ScanTime,
    t.[{_colOperator}] = u.OperatorName,
    t.[{_colResult}] = u.ResultValue,
    t.[{_colDescription}] = CASE WHEN u.MarkDone = 1 THEN @doneDescription ELSE t.[{_colDescription}] END
OUTPUT INSERTED.[{_colComputer}] INTO #done(ComputerName)
FROM {FullTable} t
JOIN #upd u
  ON CAST(t.[{_colComputer}] AS nvarchar(256)) = u.ComputerName
WHERE NULLIF(LTRIM(RTRIM(t.[{_colComputer}])), '') IS NOT NULL;"
                : $@"
UPDATE t
SET
    t.[{_colLastScan}] = u.ScanTime,
    t.[{_colOperator}] = u.OperatorName,
    t.[{_colResult}] = u.ResultValue,
    t.[{_colDescription}] = CASE WHEN u.MarkDone = 1 THEN @doneDescription ELSE t.[{_colDescription}] END
OUTPUT INSERTED.[{_colComputer}] INTO #done(ComputerName)
FROM {FullTable} t
JOIN #upd u
  ON CAST(t.[{_colComputer}] AS nvarchar(256)) = u.ComputerName
WHERE
    NULLIF(LTRIM(RTRIM(t.[{_colComputer}])), '') IS NOT NULL
    AND t.[{_colDescription}] = @targetDescription;";

            using (var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = _commandTimeoutSeconds })
            {
                cmd.Parameters.AddWithValue("@doneDescription", _doneDescriptionValue);
                if (!verboseMode)
                    cmd.Parameters.AddWithValue("@targetDescription", _targetDescriptionValue);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var doneSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SqlCommand("SELECT ComputerName FROM #done;", conn, tx) { CommandTimeout = _commandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    doneSet.Add(reader.GetString(0).Trim());
            }

            foreach (var u in updates)
            {
                if (doneSet.Contains(u.ComputerName))
                    updated.Add(u);
                else
                    skipped.Add(u.ComputerName);
            }

            await tx.CommitAsync(ct);
            return new BatchUpdateResult(updated.Count, skipped.Count, failed.Count, updated, skipped, failed);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            foreach (var u in updates)
                failed.Add(new FailedItem(u.ComputerName, ex.Message));

            return new BatchUpdateResult(0, 0, failed.Count, Array.Empty<BuildScanUpdate>(), Array.Empty<string>(), failed);
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SmartOrders.Core.Domain;
using SmartOrders.Core.Repositories;

namespace SmartOrders.Infrastructure.Repositories;

/// <summary>
/// SQLite-backed order catalog using OIDExtract.db.
/// Real schema (verified 2026-05-19):
///   Table:   orders
///   Columns: "HDROrder Code" TEXT  → item_id
///            "Order Name"    TEXT  → primary searchable field (Bob's POC approach)
///            "Display Name"  TEXT  → display label
///            "Order Category" TEXT → e.g. Lab, Imaging, Medications
///
/// Search strategy (Phase 1): multi-word OR'd LIKE on "Order Name" AND "Display Name",
/// matching Bob's POC approach in orders-db-sqljs.ts.
/// Phase 2 upgrade: swap for Azure AI Search — zero changes to callers.
/// </summary>
public sealed class SqliteOrderCatalogRepository : IOrderCatalogRepository, IAsyncDisposable
{
    private const string Table = "orders";
    private const string ColItemId = "HDROrder Code";
    private const string ColOrderName = "Order Name";
    private const string ColDisplayName = "Display Name";
    private const string ColCategory = "Order Category";

    private readonly string _dbPath;
    private readonly ILogger<SqliteOrderCatalogRepository> _logger;
    private SqliteConnection? _conn;

    public SqliteOrderCatalogRepository(string dbPath, ILogger<SqliteOrderCatalogRepository> logger)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_conn is not null) return _conn;

        if (!File.Exists(_dbPath))
            throw new CatalogSearchException(
                $"OIDExtract.db not found at '{_dbPath}'. Copy the file to the data/ folder (see README).");

        _conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        await _conn.OpenAsync(ct);
        return _conn;
    }

    /// <summary>
    /// Multi-word OR'd LIKE search against "Order Name" and "Display Name".
    /// Splits the search term on whitespace and emits one LIKE clause per word OR'd together —
    /// matching Bob's POC approach in orders-db-sqljs.ts.
    /// </summary>
    public async Task<IReadOnlyList<CatalogItem>> SearchAsync(string text, double? rplDe = null, int limit = 10,
        CancellationToken ct = default)
    {
        var clampedLimit = Math.Min(limit, 20);

        // Split on whitespace only (keep hyphenated terms like "x-ray" intact).
        // Filter single-character tokens that cause false positives.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 1)
            .ToList();

        if (words.Count == 0) words = [text.ToLowerInvariant()];

        _logger.LogDebug("catalog_search term={Text} words={Words} limit={Limit}", text, string.Join(",", words), clampedLimit);

        // Build OR clause: each word checked against Order Name and Display Name.
        // Use unique named parameters @p0, @p1, ... to avoid duplicate-name errors in SQLite.
        var clauses = string.Join(" OR ", words.Select((_, i) =>
            $"(LOWER(\"{ColOrderName}\") LIKE @p{i * 2} OR LOWER(\"{ColDisplayName}\") LIKE @p{i * 2 + 1})"));

        // Filter inactive orders — mirrors index_catalog.py WHERE IsInActiveFlag IS NULL OR != 'Y'
        var sql = $"""
            SELECT TRIM("{ColItemId}") AS item_id,
                   "{ColDisplayName}" AS display_name,
                   "{ColCategory}" AS order_category
            FROM   "{Table}"
            WHERE  ("IsInActiveFlag" IS NULL OR UPPER(TRIM("IsInActiveFlag")) != 'Y')
              AND  "{ColItemId}" IS NOT NULL AND TRIM("{ColItemId}") != ''
              AND  ({clauses})
            LIMIT  @limit
            """;

        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        for (var i = 0; i < words.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@p{i * 2}", $"%{words[i]}%");
            cmd.Parameters.AddWithValue($"@p{i * 2 + 1}", $"%{words[i]}%");
        }
        cmd.Parameters.AddWithValue("@limit", clampedLimit);

        try
        {
            var results = new List<CatalogItem>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new CatalogItem(
                    ItemId: reader.GetString(0),
                    DisplayName: reader.GetString(1),
                    OrderCategory: reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
            }

            _logger.LogDebug("catalog_search completed term={Text} hits={Hits}", text, results.Count);
            return results;
        }
        catch (SqliteException ex)
        {
            throw new CatalogSearchException($"Catalog search failed: {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            await _conn.CloseAsync();
            await _conn.DisposeAsync();
            _conn = null;
        }
    }
}

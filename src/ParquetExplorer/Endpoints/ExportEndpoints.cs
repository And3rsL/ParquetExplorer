using ParquetExplorer.Services;

namespace ParquetExplorer.Endpoints;

public sealed record ExportRequest(string Sql, string Format);

public static class ExportEndpoints
{
    private static readonly Dictionary<string, (string CopyOptions, string ContentType, string Extension)> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csv"] = ("FORMAT csv, HEADER true", "text/csv", "csv"),
        ["parquet"] = ("FORMAT parquet, COMPRESSION zstd", "application/vnd.apache.parquet", "parquet"),
        ["json"] = ("FORMAT json", "application/x-ndjson", "jsonl"),
    };

    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        // Exports the full result of a single SELECT (no row limit) by letting DuckDB stream it to a temp file.
        app.MapPost("/api/export", async (ExportRequest req, HttpContext ctx, DuckDbEngine engine, ILogger<DuckDbEngine> logger) =>
        {
            if (!Formats.TryGetValue(req.Format ?? "", out var fmt))
                return Results.BadRequest("Unknown format.");

            var statements = SqlText.SplitStatements(req.Sql ?? "");
            if (statements.Count != 1)
                return Results.BadRequest("Export needs exactly one SELECT statement.");

            var sql = statements[0];
            if (SqlGuard.CheckReadOnly(sql) is { } reason)
                return Results.BadRequest(reason);

            var file = Path.Combine(engine.ExportDirectory, $"{Guid.NewGuid():N}.{fmt.Extension}");
            try
            {
                await Task.Run(() =>
                {
                    using var conn = engine.OpenConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"COPY ({sql}) TO {SqlText.Literal(file)} ({fmt.CopyOptions})";
                    using var reg = ctx.RequestAborted.Register(() => { try { cmd.Cancel(); } catch { } });
                    cmd.ExecuteNonQuery();
                }, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                TryDelete(file);
                return Results.BadRequest(ex.Message);
            }

            logger.LogInformation("Export {Format} by {User}", req.Format, ctx.User.Identity?.Name);
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            return Results.File(stream, fmt.ContentType, $"export-{DateTime.Now:yyyyMMdd-HHmmss}.{fmt.Extension}");
        }).RequireAuthorization();
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* best effort */ }
    }
}

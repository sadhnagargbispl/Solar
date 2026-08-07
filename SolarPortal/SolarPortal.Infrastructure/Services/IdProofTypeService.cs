using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Reads active address-proof types from the legacy M_IdTypeMaster table via raw
/// ADO.NET (same pattern as BankService / StateService).
///
/// Returns an empty list rather than a fabricated one if the table cannot be
/// reached — the KYC page then just shows no options instead of the wrong ones.
/// </summary>
public class IdProofTypeService : IIdProofTypeService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;

    public IdProofTypeService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    public async Task<List<IdProofTypeDto>> GetActiveAsync()
    {
        var rows = new List<IdProofTypeDto>();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
            return rows;

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            const string sql = @"
                SELECT Id, IdType
                FROM M_IdTypeMaster
                WHERE ActiveStatus = 'Y'
                ORDER BY IdType";

            await using var cmd = new SqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var name = rdr["IdType"]?.ToString()?.Trim() ?? "";

                // The legacy master carries placeholder rows like
                // "--Choose Id Proof--" (Id = 0); the view has its own "Select".
                if (string.IsNullOrWhiteSpace(name)) continue;
                var lower = name.ToLowerInvariant();
                if (name.StartsWith("--") || lower.Contains("choose") || lower.Contains("select"))
                    continue;

                var id = int.TryParse(rdr["Id"]?.ToString(), out var i) ? i : 0;
                if (id <= 0) continue;

                rows.Add(new IdProofTypeDto { Id = id, IdType = name });
            }
        }
        catch
        {
            return new List<IdProofTypeDto>();
        }

        return rows;
    }
}

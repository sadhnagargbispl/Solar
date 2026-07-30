using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Reads active banks from the legacy M_BankMaster table via raw ADO.NET
/// (same pattern as StateService / PayModeService).
///
/// Returns an empty list rather than a fabricated one if the table cannot be
/// reached - a wrong bank name on a payout is worse than an empty dropdown.
/// </summary>
public class BankService : IBankService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;

    public BankService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    public async Task<List<BankDto>> GetActiveAsync()
    {
        var rows = new List<BankDto>();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
            return rows;

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            const string sql = @"
                SELECT BId, BankName
                FROM M_BankMaster
                WHERE ActiveStatus = 'Y' AND RowStatus = 'Y'
                ORDER BY BankName";

            await using var cmd = new SqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var name = rdr["BankName"]?.ToString()?.Trim() ?? "";

                // Skip the placeholder/header rows the legacy table carries
                // (e.g. "--Choose Bank Name--"); the view has its own "Select".
                if (string.IsNullOrWhiteSpace(name)) continue;
                var lower = name.ToLowerInvariant();
                if (name.StartsWith("--") ||
                    lower.Contains("choose") ||
                    lower.Contains("select"))
                    continue;

                rows.Add(new BankDto
                {
                    BId = int.TryParse(rdr["BId"]?.ToString(), out var id) ? id : 0,
                    BankName = name
                });
            }
        }
        catch
        {
            return new List<BankDto>();
        }

        return rows;
    }
}

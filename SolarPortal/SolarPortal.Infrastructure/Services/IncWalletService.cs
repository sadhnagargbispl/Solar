using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// INC commission + wallet.
///
/// Ledger: dbo.IncTrnvoucher - legacy dbo.TrnVoucher ki hu-ba-hu copy, par alag
/// table. Legacy wallet ko chhua nahi jata.
///
/// Wallet kiski: jo INC user login kiya hai uski Worker Id. Credit par CrTo,
/// withdrawal par DrTo - dono mein wahi id.
///
/// Raw SQL (not EF) on purpose: IncTrnvoucher / IncVouchertype are created by
/// ADD-IncWallet.sql, and IncCommissionLedger belongs to the admin panel app.
/// None of them are in our EF model and migrations must not touch them. Same
/// pattern as BasicProductService / LegacyProductRequestService.
/// </summary>
public class IncWalletService : IIncWalletService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;   // only for the connection-string fallback

    /// <summary>
    /// TDS on the per-connection (IncConnections) income only. Installation
    /// commission is credited flat, exactly as set in SolarProjects.IncCommissionAmount.
    /// </summary>
    public const decimal TdsPercent = 1m;

    /// <summary>Wallet code in IncVouchertype.</summary>
    private const string IncWalletAcType = "I";

    public IncWalletService(IConfiguration config, ApplicationDbContext db)
    {
        _config = config;
        _db = db;
    }

    private string? ConnStr => _config.GetConnectionString("DefaultConnection")
                            ?? _db.Database.GetConnectionString();

    public async Task<IncCommissionResult> CreditInstallationCommissionAsync(
        int solarRequestId, int workerId, string performedBy)
    {
        var result = new IncCommissionResult();
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr) || solarRequestId <= 0 || workerId <= 0)
        {
            result.Message = "Commission skipped: missing request or worker.";
            return result;
        }

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        // Only INC workers earn installation commission - JOB workers do the same
        // work on salary. The caller's WorkerType claim is stamped into the auth
        // cookie at login and goes stale the moment admin switches a worker's type,
        // so the type is re-read from Workers here. This method is the only place
        // installation commission is written, so this is where the rule has to hold.
        await using (var wt = conn.CreateCommand())
        {
            wt.CommandText = "SELECT Type FROM dbo.Workers WHERE Id = @w AND IsDeleted = 0";
            wt.Parameters.Add(new SqlParameter("@w", workerId));
            var t = await wt.ExecuteScalarAsync();
            if (t == null || t == DBNull.Value)
            {
                result.Message = "Commission skipped: worker not found.";
                return result;
            }
            if (Convert.ToInt32(t) != (int)SolarPortal.Domain.Enums.WorkerType.INC)
            {
                result.Message = "Commission skipped: only INC workers earn installation commission.";
                return result;
            }
        }

        // Already credited? Mark Complete can be retried, so never double-pay.
        // Keyed on the request alone - one project earns its commission once.
        await using (var dup = conn.CreateCommand())
        {
            dup.CommandText = "SELECT COUNT(1) FROM dbo.IncCommissionLedger WHERE SolarRequestId = @r";
            dup.Parameters.Add(new SqlParameter("@r", solarRequestId));
            if (Convert.ToInt32(await dup.ExecuteScalarAsync() ?? 0) > 0)
            {
                result.Message = "Commission for this project was already credited.";
                return result;
            }
        }

        int? projectId;
        string requestNumber;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT SolarProjectId, RequestNumber FROM dbo.SolarRequests WHERE Id = @r";
            q.Parameters.Add(new SqlParameter("@r", solarRequestId));
            await using var rd = await q.ExecuteReaderAsync();
            if (!await rd.ReadAsync())
            {
                result.Message = "Commission skipped: request not found.";
                return result;
            }
            projectId     = rd.IsDBNull(0) ? null : Convert.ToInt32(rd.GetValue(0));
            requestNumber = rd.IsDBNull(1) ? string.Empty : (rd.GetValue(1)?.ToString() ?? string.Empty).Trim();
        }

        if (projectId is null or 0)
        {
            result.Message = "Commission skipped: this project has no solar plan attached.";
            return result;
        }

        // Flat commission set on the plan itself. Exactly this much is credited -
        // no percentage maths, no deduction.
        decimal commission = 0m;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT IncCommissionAmount FROM dbo.SolarProjects WHERE Id = @p";
            q.Parameters.Add(new SqlParameter("@p", projectId.Value));
            var v = await q.ExecuteScalarAsync();
            if (v != null && v != DBNull.Value) commission = Convert.ToDecimal(v);
        }

        if (commission <= 0m)
        {
            result.Message = "Commission skipped: no INC commission amount is set on this plan.";
            return result;
        }

        result.GrossAmount = commission;
        result.TdsAmount   = 0m;
        result.NetAmount   = commission;

        var narration = $"INC commission for {requestNumber}";
        var refNo     = $"INC/{requestNumber}";

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = @"
INSERT INTO dbo.IncCommissionLedger
    (WorkerId, SolarRequestId, GrossAmount, TdsPercent, TdsAmount, NetAmount, CreditedAt, Status, Notes)
VALUES
    (@w, @r, @amt, 0, 0, @amt, @now, 'Credited', @notes)";
                ins.Parameters.Add(new SqlParameter("@w", workerId));
                ins.Parameters.Add(new SqlParameter("@r", solarRequestId));
                ins.Parameters.Add(new SqlParameter("@amt", commission));
                ins.Parameters.Add(new SqlParameter("@now", DateTime.Now));
                ins.Parameters.Add(new SqlParameter("@notes", $"{narration}. Marked complete by {performedBy}."));
                await ins.ExecuteNonQueryAsync();
            }

            await PostVoucherAsync(conn, tx, "C", creditTo: workerId.ToString(), debitFrom: "0",
                                   amount: commission, narration: narration, refNo: refNo,
                                   workerId: workerId, date: DateTime.Now);
            result.WalletPosted = true;

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        result.Credited = true;
        result.Message  = $"Commission {result.NetAmount:N2} credited to your INC wallet.";
        return result;
    }

    public async Task<(bool posted, string message)> DebitForWithdrawalAsync(
        int workerId, decimal amount, string withdrawalRef)
    {
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr) || workerId <= 0 || amount <= 0m)
            return (false, "Wallet debit skipped: invalid worker or amount.");

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        var refNo = $"{withdrawalRef}/{workerId}";

        // Idempotent: one debit per withdrawal request.
        await using (var dup = conn.CreateCommand())
        {
            dup.CommandText = "SELECT COUNT(1) FROM dbo.IncTrnvoucher WHERE AcType = @actype AND RefNo = @ref";
            dup.Parameters.Add(new SqlParameter("@actype", IncWalletAcType));
            dup.Parameters.Add(new SqlParameter("@ref", refNo));
            if (Convert.ToInt32(await dup.ExecuteScalarAsync() ?? 0) > 0)
                return (false, "Wallet already debited for this withdrawal request.");
        }

        await PostVoucherAsync(conn, null, "W", creditTo: "0", debitFrom: workerId.ToString(),
                               amount: amount,
                               narration: $"Fund debited against INC wallet withdrawal request {withdrawalRef} on {DateTime.Now:dd-MMM-yyyy}",
                               refNo: refNo, workerId: workerId, date: DateTime.Now);

        return (true, $"{amount:N2} debited from your INC wallet.");
    }

    /// <summary>
    /// Writes one IncTrnvoucher row. VoucherId is an identity; VoucherNo continues
    /// the running series the legacy app style. UPDLOCK/HOLDLOCK stops two
    /// concurrent posts picking the same number.
    /// </summary>
    private async Task PostVoucherAsync(SqlConnection conn, SqlTransaction? tx, string vType,
        string creditTo, string debitFrom, decimal amount, string narration, string refNo,
        int workerId, DateTime date)
    {
        await using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO dbo.IncTrnvoucher
    (VoucherNo, VoucherDate, DrTo, CrTo, Amount, Narration, RefNo, AcType,
     RecTimeStamp, VType, SessID, WSessID, Balance, UserId, FromID)
SELECT ISNULL(MAX(VoucherNo), 0) + 1, @vdate, @drTo, @crTo, @amt, @narr, @ref, @actype,
       GETDATE(), @vtype, @sess, 1, 0, @uid, NULL
FROM dbo.IncTrnvoucher WITH (UPDLOCK, HOLDLOCK)";
        cmd.Parameters.Add(new SqlParameter("@vdate", date.Date));
        cmd.Parameters.Add(new SqlParameter("@drTo", debitFrom));
        cmd.Parameters.Add(new SqlParameter("@crTo", creditTo));
        cmd.Parameters.Add(new SqlParameter("@amt", amount));
        cmd.Parameters.Add(new SqlParameter("@narr", narration));
        cmd.Parameters.Add(new SqlParameter("@ref", refNo));
        cmd.Parameters.Add(new SqlParameter("@actype", IncWalletAcType));
        cmd.Parameters.Add(new SqlParameter("@vtype", vType));
        cmd.Parameters.Add(new SqlParameter("@sess", Convert.ToDecimal(date.ToString("yyyyMMdd"))));
        cmd.Parameters.Add(new SqlParameter("@uid", workerId));
        await cmd.ExecuteNonQueryAsync();
    }

    // Wallet statement for the logged-in INC user: every credit and debit on his id.
    public async Task<List<IncWalletEntry>> GetWalletEntriesAsync(int workerId)
    {
        var rows = new List<IncWalletEntry>();
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr) || workerId <= 0) return rows;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT VoucherNo, VoucherDate, Narration, Amount, VType, RefNo
FROM dbo.IncTrnvoucher
WHERE AcType = @actype
  AND (LTRIM(RTRIM(CrTo)) = @id OR LTRIM(RTRIM(DrTo)) = @id)
ORDER BY VoucherId DESC";
        cmd.Parameters.Add(new SqlParameter("@id", workerId.ToString()));
        cmd.Parameters.Add(new SqlParameter("@actype", IncWalletAcType));

        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            rows.Add(new IncWalletEntry
            {
                VoucherNo = rd.IsDBNull(0) ? string.Empty : (rd.GetValue(0)?.ToString() ?? string.Empty).Trim(),
                Date      = rd.IsDBNull(1) ? DateTime.MinValue : Convert.ToDateTime(rd.GetValue(1)),
                Narration = rd.IsDBNull(2) ? string.Empty : (rd.GetValue(2)?.ToString() ?? string.Empty).Trim(),
                Amount    = rd.IsDBNull(3) ? 0m : Convert.ToDecimal(rd.GetValue(3)),
                IsCredit  = !rd.IsDBNull(4) && string.Equals((rd.GetValue(4)?.ToString() ?? string.Empty).Trim(), "C", StringComparison.OrdinalIgnoreCase),
                RefNo     = rd.IsDBNull(5) ? string.Empty : (rd.GetValue(5)?.ToString() ?? string.Empty).Trim()
            });
        }
        return rows;
    }

    public async Task<decimal> GetLedgerNetAsync(int workerId)
    {
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr) || workerId <= 0) return 0m;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ISNULL(SUM(NetAmount), 0) FROM dbo.IncCommissionLedger
                            WHERE WorkerId = @w AND Status <> 'Reversed'";
        cmd.Parameters.Add(new SqlParameter("@w", workerId));
        var v = await cmd.ExecuteScalarAsync();
        return v == null || v == DBNull.Value ? 0m : Convert.ToDecimal(v);
    }

    // Credits minus debits on this INC user's wallet.
    public async Task<decimal> GetWalletBalanceAsync(int workerId)
    {
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr) || workerId <= 0) return 0m;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT ISNULL(SUM(CASE WHEN VType = 'C' AND LTRIM(RTRIM(CrTo)) = @id THEN Amount
                       WHEN VType <> 'C' AND LTRIM(RTRIM(DrTo)) = @id THEN -Amount
                       ELSE 0 END), 0)
FROM dbo.IncTrnvoucher
WHERE AcType = @actype AND (LTRIM(RTRIM(CrTo)) = @id OR LTRIM(RTRIM(DrTo)) = @id)";
        cmd.Parameters.Add(new SqlParameter("@id", workerId.ToString()));
        cmd.Parameters.Add(new SqlParameter("@actype", IncWalletAcType));
        var bal = await cmd.ExecuteScalarAsync();
        return bal == null || bal == DBNull.Value ? 0m : Convert.ToDecimal(bal);
    }
}

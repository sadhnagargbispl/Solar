using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SolarPortal.Application.Services;
using SolarPortal.Infrastructure.Data;

namespace SolarPortal.Infrastructure.Services;

/// <summary>
/// Inserts "With Activation" submissions into the legacy SolFit table
/// TrnProductorderDetail via raw ADO.NET, mirroring the original VB
/// page ProductWalletRequest.aspx.vb (tp=A branch).
///
/// Reads the picked product's master row from [V#SpProductDetail] to get
/// MRP/DP/BV/PV/ShippingAmount/ProductName/ShippingProdid — same view the
/// new "With Activation" UI uses for the product cards. The insert mirrors
/// the legacy column list exactly so existing reports keep working.
///
/// We do NOT fail the user submission if the legacy insert errors — the
/// new SolarRequest record is the source of truth for the new workflow.
/// Legacy bridge errors are logged and surfaced as a warning so admin can
/// reconcile manually if needed.
/// </summary>
public class LegacyProductRequestService : ILegacyProductRequestService
{
    private readonly IConfiguration _config;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<LegacyProductRequestService> _log;

    public LegacyProductRequestService(
        IConfiguration config,
        ApplicationDbContext db,
        ILogger<LegacyProductRequestService> log)
    {
        _config = config;
        _db     = db;
        _log    = log;
    }

    public async Task<LegacyInsertResult> InsertWithActivationAsync(LegacyProductRequestInput input)
    {
        var result = new LegacyInsertResult();
        var connStr = _config.GetConnectionString("DefaultConnection")
                   ?? _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            result.ErrorMessage = "No connection string configured for legacy bridge.";
            return result;
        }

        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // ─── 1. Resolve Formno from IdNo ─────────────────────────────
            // The legacy table is keyed by Formno (numeric), but the new app
            // identifies users by IdNo (e.g. "SE86372259"). m_membermaster
            // bridges the two — and we already login via that table.
            long formNo;
            await using (var cmdF = new SqlCommand(
                "SELECT TOP 1 Formno FROM M_MemberMaster WHERE Idno = @id", conn))
            {
                cmdF.Parameters.AddWithValue("@id", input.MemberIdNo ?? string.Empty);
                var fObj = await cmdF.ExecuteScalarAsync();
                if (fObj == null || fObj == DBNull.Value)
                {
                    result.ErrorMessage = $"Legacy bridge: member IdNo '{input.MemberIdNo}' not found in M_MemberMaster.";
                    _log.LogWarning(result.ErrorMessage);
                    return result;
                }
                formNo = Convert.ToInt64(fObj);
            }

            // ─── 2. Generate a unique OrderNo ────────────────────────────
            // The legacy VB code uses {6 random digits}+{Formno} and retries
            // on collision. We do the same up to 5 attempts.
            string? orderNo = null;
            var rng = new Random();
            for (int i = 0; i < 5; i++)
            {
                var candidate = $"{rng.Next(100000, 999999)}{formNo}";
                await using var cmdC = new SqlCommand(
                    "SELECT COUNT(*) FROM TrnOrder WHERE Orderno = @o", conn);
                cmdC.Parameters.AddWithValue("@o", candidate);
                var cnt = (int)(await cmdC.ExecuteScalarAsync() ?? 0);
                if (cnt == 0) { orderNo = candidate; break; }
            }
            if (orderNo == null)
            {
                result.ErrorMessage = "Legacy bridge: couldn't allocate a unique OrderNo after 5 attempts.";
                _log.LogWarning(result.ErrorMessage);
                return result;
            }

            // ─── 3. Insert the order-detail row ──────────────────────────
            // Column list matches the legacy VB Insert exactly. We pull
            // product master fields from [V#SpProductDetail] inline so the
            // BV/PV/MRP/DP/Shipping numbers are authoritative (same source
            // the With Activation cards display).
            const string insertSql = @"
INSERT INTO TrnProductorderDetail
    (OrderNo, FormNo, ProductID, Qty, Rate, NetAmount, RecTimeStamp,
     DispDate, DispStatus, DispQty, RemQty, DispAmt,
     MRP, DP, ProductName, ImgPath, RP, BV,
     FSEssId, Prodtype, PV, txnid, txndate, ImageUpload,
     ForType, PID,
     UserAddress, City, District, PinCode, UserState, StateCode)
SELECT
    @OrderNo, @FormNo, ProdId, @Qty, DP, DP * @Qty, GETDATE(),
    NULL, 'N', 0, @Qty, 0,
    MRP, DP, ProductName, '', 0, BV,
    (SELECT ISNULL(MAX(FsessID), 1) FROM solfitenergyinv..M_FiscalMaster),
    'P', PV, @TxnId, @TxnDate, @ImageFile,
    'A', @PayMode,
    @Addr, @City, @Dist, @Pin, @StateNm, @StateCd
FROM [V#SpProductDetail]
WHERE ProdId = @ProdId;";

            await using var cmdI = new SqlCommand(insertSql, conn);
            cmdI.Parameters.AddWithValue("@OrderNo",   orderNo);
            cmdI.Parameters.AddWithValue("@FormNo",    formNo);
            cmdI.Parameters.AddWithValue("@ProdId",    input.ProductId);
            cmdI.Parameters.AddWithValue("@Qty",       Math.Max(1, input.Qty));
            cmdI.Parameters.AddWithValue("@TxnId",     (object?)input.TxnId ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@TxnDate",   (object?)input.TxnDate ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@ImageFile", (object?)input.ImageFileName ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@PayMode",   input.PayModeId);
            cmdI.Parameters.AddWithValue("@Addr",      (object?)input.Address ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@City",      (object?)input.City ?? DBNull.Value);
            cmdI.Parameters.AddWithValue("@Dist",      (object?)input.District ?? DBNull.Value);
            // ===== PinCode: legacy column is numeric(18) =====
            // The profile PinCode is free-text in the new app and is often blank
            // (''), which SQL cannot convert to numeric — that exact case produced
            //   "Error converting data type nvarchar to numeric"
            // when a user activated their ID (SCR-018 had PinCode = ''). Strip
            // non-digits; blank/unparseable → NULL (the legacy column is nullable).
            object pinParam = DBNull.Value;
            if (!string.IsNullOrWhiteSpace(input.PinCode))
            {
                var pinDigits = new string(input.PinCode.Where(char.IsDigit).ToArray());
                if (pinDigits.Length > 0 &&
                    decimal.TryParse(pinDigits, System.Globalization.NumberStyles.None,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out var pinVal))
                {
                    pinParam = pinVal;
                }
            }
            cmdI.Parameters.Add(new SqlParameter("@Pin", System.Data.SqlDbType.Decimal)
            {
                Precision = 18,
                Scale     = 0,
                Value     = pinParam
            });
            cmdI.Parameters.AddWithValue("@StateNm",   (object?)input.StateName ?? DBNull.Value);

            // ===== Resolve numeric StateCode for legacy column =====
            // The legacy TrnProductorderDetail.StateCode column is **numeric**,
            // but the controller passes whatever it has (often the state NAME
            // like "Rajasthan" because the form doesn't collect a separate state
            // code). Passing a non-numeric string blew up with
            //   "Error converting data type nvarchar to numeric".
            //
            // Resolution strategy:
            //   1. If input.StateCode parses cleanly as decimal → use it
            //   2. Else, look it up from M_StateDivMaster by StateName
            //   3. Else, send DBNull (column is nullable on the legacy side)
            object stateCodeParam = await ResolveStateCodeAsync(input.StateCode, input.StateName);
            var stateCdSqlParam = new SqlParameter("@StateCd", System.Data.SqlDbType.Decimal)
            {
                Precision = 18,
                Scale     = 0,
                Value     = stateCodeParam
            };
            cmdI.Parameters.Add(stateCdSqlParam);

            var rows = await cmdI.ExecuteNonQueryAsync();
            if (rows < 1)
            {
                result.ErrorMessage = $"Legacy bridge: product Id {input.ProductId} not found in V#SpProductDetail.";
                _log.LogWarning(result.ErrorMessage);
                return result;
            }

            result.Success = true;
            result.OrderNo = orderNo;
            _log.LogInformation("Legacy product order created: OrderNo={OrderNo}, FormNo={FormNo}, ProductId={ProdId}, Qty={Qty}",
                orderNo, formNo, input.ProductId, input.Qty);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Legacy bridge insert failed for IdNo={IdNo}, ProductId={ProdId}",
                input.MemberIdNo, input.ProductId);
            result.ErrorMessage = $"Legacy bridge insert failed: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Resolves the numeric value to bind to @StateCd. The legacy column is
    /// decimal/numeric, but the controller often passes the state NAME (e.g.
    /// "Rajasthan") because the user form doesn't collect a separate code.
    ///
    /// Priority:
    ///   1. If the caller-supplied StateCode parses cleanly as decimal → use it.
    ///   2. Else look up M_StateDivMaster by StateName (case-insensitive,
    ///      whitespace-trimmed). Returns the matching StateCode.
    ///   3. Else fall back to DBNull so SQL doesn't choke on type mismatch.
    /// </summary>
    private async Task<object> ResolveStateCodeAsync(string? rawCode, string? stateName)
    {
        // 1. Did the caller already give us a numeric code?
        if (!string.IsNullOrWhiteSpace(rawCode) &&
            decimal.TryParse(rawCode.Trim(), System.Globalization.NumberStyles.Number,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out var parsed))
        {
            return parsed;
        }

        // 2. Lookup by state name in M_StateDivMaster
        if (!string.IsNullOrWhiteSpace(stateName))
        {
            var name = stateName.Trim();
            var row = await _db.States
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.StateName != null && s.StateName.Trim() == name);

            if (row != null) return row.StateCode;

            // Try case-insensitive match as fallback (state names sometimes
            // arrive with different casing from the dropdown vs DB)
            var allStates = await _db.States.AsNoTracking().ToListAsync();
            var ci = allStates.FirstOrDefault(s =>
                s.StateName != null &&
                string.Equals(s.StateName.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (ci != null) return ci.StateCode;

            _log.LogWarning("Legacy bridge: state name '{StateName}' not found in M_StateDivMaster; sending NULL StateCode.",
                stateName);
        }

        // 3. Give up — let SQL store NULL rather than blow up on type conversion
        return DBNull.Value;
    }
}

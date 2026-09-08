namespace SolarPortal.Application.Services;

/// <summary>Everything the "Update Remaining BV" screen shows above the form.</summary>
public class RemainingBvSnapshot
{
    /// <summary>Full BV of the plan taken - SolarProjects.FinalBV.</summary>
    public decimal TotalBV { get; set; }

    /// <summary>What the solar request already consumed - the verified order's BV.</summary>
    public decimal FixedBV { get; set; }

    /// <summary>What is left to distribute across the income heads.</summary>
    public decimal RemainingBV { get; set; }

    /// <summary>Readable trace of where the two figures came from.</summary>
    public string Source { get; set; } = string.Empty;

    // ─── Money each head pays, straight from the SolarProjects master ─────
    // select DiscomWork as [Discom Income], DealClose as [Deal Close],
    //        SCZMenue   as [SCI Income]
    // from   SolarProjects where IsActive = 1
    //
    // Display only: the member picks WHO receives each head, never how much.
    public decimal DiscomIncomeAmount { get; set; }
    public decimal DealCloseAmount { get; set; }
    public decimal SciIncomeAmount { get; set; }

    public decimal TotalIncome => DiscomIncomeAmount + DealCloseAmount + SciIncomeAmount;

    public bool HasRemaining => RemainingBV > 0m;
}

/// <summary>
/// One place that decides what "Remaining BV" means, so the member page, the admin
/// correction page and any later payout run can never disagree about it.
///
/// TOTAL comes from the selected plan (SolarProjects.FinalBV, e.g. 110 / 175).
/// FIXED is the BV the solar request already consumed, which is exactly the BV on
/// the member's verified TrnProductorderDetail row (100) - that part is credited
/// by the normal product flow, so this screen must not hand it out again.
/// REMAINING is the difference, and that is what gets distributed.
///
/// Getting the priority the wrong way round makes the product's 100 the total and
/// leaves nothing to distribute, which is exactly the bug this ordering prevents.
/// </summary>
public static class RemainingBvCalculator
{
    /// <summary>
    /// Fallback for the fixed part when neither the verified order nor the plan
    /// carries a BV. Matches SolarProject.BV's default.
    /// </summary>
    public const decimal SolarRequestFixedBv = 100m;

    /// <param name="orderBV">BV on the member's verified product order.</param>
    /// <param name="projectBV">SolarProjects.BV of the selected plan.</param>
    /// <param name="projectFinalBV">SolarProjects.FinalBV of the selected plan.</param>
    /// <param name="projectId">Only used to make the Source line traceable.</param>
    /// <param name="discomWork">SolarProjects.DiscomWork - the Discom Income amount.</param>
    /// <param name="dealClose">SolarProjects.DealClose - the Deal Close amount.</param>
    /// <param name="sczMenue">SolarProjects.SCZMenue - the SCI Income amount.</param>
    public static RemainingBvSnapshot From(
        decimal orderBV,
        int? projectBV,
        int? projectFinalBV,
        int? projectId,
        decimal discomWork,
        decimal dealClose,
        decimal sczMenue)
    {
        // ── FIXED: what the solar request already ate ─────────────────────
        // The verified order's BV is the most reliable figure because it is what
        // was actually credited. Fall back to the plan's BV, then to the spec's 100.
        var fixedBv = orderBV;
        if (fixedBv <= 0m && projectBV is > 0) fixedBv = projectBV.Value;
        if (fixedBv <= 0m) fixedBv = SolarRequestFixedBv;

        // ── TOTAL: the whole BV of the plan taken ─────────────────────────
        decimal total;
        string source;

        if (projectFinalBV is > 0)
        {
            total = projectFinalBV.Value;
            source = $"Plan Final BV {total:N2} (solar project #{projectId})";
        }
        else if (projectBV is > 0)
        {
            total = projectBV.Value;
            source = $"Plan BV {total:N2} (solar project #{projectId})";
        }
        else
        {
            total = orderBV;
            source = $"Product order BV {total:N2} (no solar project on this request)";
        }

        source += $", less {fixedBv:N2} already taken by the solar request";

        // The fixed part can never exceed the total, which would otherwise produce
        // a negative remainder on a plan configured below the product's BV.
        if (fixedBv > total) fixedBv = total;

        return new RemainingBvSnapshot
        {
            TotalBV = total,
            FixedBV = fixedBv,
            RemainingBV = Math.Max(0m, total - fixedBv),
            Source = source,
            DiscomIncomeAmount = discomWork,
            DealCloseAmount = dealClose,
            SciIncomeAmount = sczMenue
        };
    }
}

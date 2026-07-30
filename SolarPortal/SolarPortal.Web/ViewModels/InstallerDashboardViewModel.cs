namespace SolarPortal.Web.ViewModels;

/// <summary>
/// Numbers behind the installer / INC dashboard tiles. These used to be hard-coded
/// to 0 in the view, so an installer with assigned work still saw an empty panel.
/// </summary>
public class InstallerDashboardViewModel
{
    public bool IsInc { get; set; }
    public string WorkerName { get; set; } = "Installer";

    // Connections the worker registered himself (New Installer -> Reports).
    public int RegisteredConnections { get; set; }
    public int RegisteredAwaitingApproval { get; set; }

    // Projects the admin assigned to this worker (Installations page).
    public int AssignedProjects { get; set; }
    public int AssignedPending { get; set; }

    // Tile totals - the worker's whole workload, from both sources.
    public int TotalConnections => RegisteredConnections + AssignedProjects;
    public int TotalPending => RegisteredAwaitingApproval + AssignedPending;

    // INC income - same maths as the Withdraw page: 1% TDS, minus whatever has
    // already been requested or paid out.
    public decimal NetIncome { get; set; }
    public decimal AvailableToWithdraw { get; set; }
}

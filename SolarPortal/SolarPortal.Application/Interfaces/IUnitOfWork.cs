using SolarPortal.Application.Interfaces.Repositories;

namespace SolarPortal.Application.Interfaces;

public interface IUnitOfWork : IDisposable
{
    ISolarRequestRepository SolarRequests { get; }
    IGenericRepository<Domain.Entities.Payment> Payments { get; }
    IGenericRepository<Domain.Entities.Document> Documents { get; }
    IGenericRepository<Domain.Entities.Worker> Workers { get; }
    IGenericRepository<Domain.Entities.SiteSurvey> SiteSurveys { get; }
    IGenericRepository<Domain.Entities.MeterDispatch> MeterDispatches { get; }
    IGenericRepository<Domain.Entities.MaterialDispatch> MaterialDispatches { get; }
    IGenericRepository<Domain.Entities.Installation> Installations { get; }
    IGenericRepository<Domain.Entities.WorkerAssignment> WorkerAssignments { get; }
    IGenericRepository<Domain.Entities.DCRDocument> DCRDocuments { get; }
    IGenericRepository<Domain.Entities.Commission> Commissions { get; }
    IGenericRepository<Domain.Entities.Notification> Notifications { get; }
    Task<int> SaveChangesAsync();
    Task BeginTransactionAsync();
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();
}
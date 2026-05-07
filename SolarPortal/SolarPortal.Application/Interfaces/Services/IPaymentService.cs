using SolarPortal.Application.DTOs;

namespace SolarPortal.Application.Interfaces.Services;

public interface IPaymentService
{
    Task<ServiceResult<PaymentDto>> CreateAsync(CreatePaymentDto dto);
    Task<IEnumerable<PaymentDto>> GetByRequestIdAsync(int requestId);
    Task<ServiceResult<bool>> VerifyAsync(int paymentId, string verifiedBy);
}

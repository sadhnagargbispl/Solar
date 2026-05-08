using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Application.Interfaces;
using SolarPortal.Application.Interfaces.Services;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Services;

public class PaymentService : IPaymentService
{
    private readonly IUnitOfWork _uow;
    private readonly IMapper _mapper;

    public PaymentService(IUnitOfWork uow, IMapper mapper)
    {
        _uow = uow;
        _mapper = mapper;
    }

    public async Task<ServiceResult<PaymentDto>> CreateAsync(CreatePaymentDto dto)
    {
        try
        {
            // Generate receipt number
            var count = await _uow.Payments.CountAsync() + 1;
            var receiptNumber = $"RCP-{DateTime.Now:yyyy}-{count:D4}";

            var payment = new Payment
            {
                SolarRequestId = dto.SolarRequestId,
                UserId = dto.UserId ?? string.Empty,
                Amount = dto.Amount,
                UTRNumber = dto.UTRNumber ?? string.Empty,
                ReferenceNumber = dto.ReferenceNumber,
                PaymentMethod = dto.PaymentMethod,
                Notes = dto.Notes,
                PaymentDate = dto.PaymentDate,
                ReceiptImagePath = dto.ReceiptImagePath,
                ReceiptNumber = receiptNumber,
                Status = PaymentStatus.Pending,
                IsVerified = false
            };

            await _uow.Payments.AddAsync(payment);
            await _uow.SaveChangesAsync();

            var result = _mapper.Map<PaymentDto>(payment);
            return ServiceResult<PaymentDto>.Success(result, "Payment submitted successfully. Awaiting verification.");
        }
        catch (Exception ex)
        {
            return ServiceResult<PaymentDto>.Failure($"Failed to submit payment: {ex.Message}");
        }
    }

    public async Task<IEnumerable<PaymentDto>> GetByRequestIdAsync(int requestId)
    {
        var payments = await _uow.Payments.FindAsync(p => p.SolarRequestId == requestId);
        return _mapper.Map<IEnumerable<PaymentDto>>(payments.OrderByDescending(p => p.CreatedAt));
    }

    public async Task<ServiceResult<bool>> VerifyAsync(int paymentId, string verifiedBy)
    {
        var payment = await _uow.Payments.GetByIdAsync(paymentId);
        if (payment == null)
            return ServiceResult<bool>.Failure("Payment not found");

        payment.IsVerified = true;
        payment.VerifiedBy = verifiedBy;
        payment.VerifiedAt = DateTime.UtcNow;
        payment.Status = PaymentStatus.Completed;
        _uow.Payments.Update(payment);
        await _uow.SaveChangesAsync();

        return ServiceResult<bool>.Success(true, "Payment verified");
    }
}
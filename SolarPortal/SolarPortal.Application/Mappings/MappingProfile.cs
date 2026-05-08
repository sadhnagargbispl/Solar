using AutoMapper;
using SolarPortal.Application.DTOs;
using SolarPortal.Domain.Entities;
using SolarPortal.Domain.Enums;

namespace SolarPortal.Application.Mappings;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // SolarRequest
        CreateMap<SolarRequest, SolarRequestDto>()
            .ForMember(d => d.UserFullName,
                o => o.MapFrom(s => s.User != null ? s.User.FullName : string.Empty))
            .ForMember(d => d.RequestedAmount, o => o.MapFrom(s => s.PlanAmount))
            .ForMember(d => d.ApprovedAmount,
                o => o.MapFrom(s => s.ApprovalStatus == ApprovalStatus.Approved ? s.PlanAmount : 0m))
            .ForMember(d => d.TotalPaid, o => o.Ignore())
            .ForMember(d => d.TotalDue, o => o.Ignore())
            .ForMember(d => d.Documents, o => o.MapFrom(s => s.Documents));

        CreateMap<CreateSolarRequestDto, SolarRequest>()
            .ForMember(d => d.Id, o => o.Ignore())
            .ForMember(d => d.RequestNumber, o => o.Ignore())
            .ForMember(d => d.UserId, o => o.Ignore())
            .ForMember(d => d.CurrentStage, o => o.Ignore())
            .ForMember(d => d.ApprovalStatus, o => o.Ignore());

        // Payment
        CreateMap<Payment, PaymentDto>()
            .ForMember(d => d.RequestNumber,
                o => o.MapFrom(s => s.SolarRequest != null ? s.SolarRequest.RequestNumber : string.Empty));

        // Document
        CreateMap<Document, DocumentDto>();

        // Notification
        CreateMap<Notification, NotificationDto>();

        // Worker
        CreateMap<Worker, WorkerDto>()
            .ForMember(d => d.AssignmentsCount,
                o => o.MapFrom(s => s.Assignments != null ? s.Assignments.Count : 0));
        CreateMap<CreateWorkerDto, Worker>()
            .ForMember(d => d.Id, o => o.Ignore());
    }
}

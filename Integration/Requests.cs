using System.ComponentModel.DataAnnotations;
namespace Nutragea.Api.Contracts;
public record LoginRequest([Required, StringLength(100)] string Username, [Required, StringLength(128)] string Password);
public record UserRequest([Required, RegularExpression(@"^[a-zA-Z0-9._-]{3,100}$")] string Username,
    [Required, StringLength(128, MinimumLength=12)] string Password,
    [Required, RegularExpression("^(Admin|Operator|QualityReviewer)$")] string Role);
public record PasswordRequest([Required, StringLength(128)] string CurrentPassword, [Required, StringLength(128, MinimumLength=12)] string NewPassword);
public record UserAccessRequest(bool IsActive, [Required, RegularExpression("^(Admin|Operator|QualityReviewer)$")] string Role);
public record GrowerRequest([Required, StringLength(150)] string Name, [StringLength(30)] string? Phone,
    [Required, StringLength(150)] string Village, [Required, StringLength(150)] string District, bool IsActive=true);
public record ProductRequest([Required, StringLength(150)] string Name, [Required, RegularExpression("^(Fresh|Dried)$")] string Stage, long? SourceProductId=null, bool IsActive=true);
public record CustomerRequest([Required, StringLength(150)] string Name, [StringLength(100)] string? Contact, [StringLength(500)] string? Address, bool IsActive=true);
public record ReceiptRequest([Range(1,long.MaxValue)] long GrowerId, DateTimeOffset ReceivedAt,
    [Required, StringLength(100)] string SupplierReference, [Required, MinLength(1), MaxLength(50)] List<ReceiptItemRequest> Items);
public record ReceiptItemRequest([Range(1,long.MaxValue)] long ProductId,
    [Range(typeof(decimal),"0.001","1000000")] decimal ReceivedKg,
    [Range(typeof(decimal),"0.001","1000000")] decimal AcceptedKg,
    [Range(typeof(decimal),"0","1000000")] decimal RejectedKg,
    [Range(typeof(decimal),"0","100000000")] decimal PurchaseAmount,
    [StringLength(500)] string? RejectionReason);
public record BatchRequest([Required, StringLength(100)] string ProcessType,
    [Required, MinLength(1), MaxLength(50)] List<BatchInputRequest> Inputs);
public record BatchInputRequest([Range(1,long.MaxValue)] long ReceiptItemId,
    [Range(typeof(decimal),"0.001","1000000")] decimal QuantityKg);
public record CompleteBatchRequest([Range(1,long.MaxValue)] long OutputProductId,
    [Range(typeof(decimal),"0.001","1000000")] decimal OutputKg,
    [Range(typeof(decimal),"0","1000000")] decimal ProcessReductionKg,
    [Range(typeof(decimal),"0","1000000")] decimal RejectedKg, [Required, StringLength(1000)] string Notes);
public record CheckRequest([Required, StringLength(100)] string CheckType, [Required, StringLength(2000)] string Result,
    [StringLength(200)] string? DocumentReference);
public record QualityRequest([Required, RegularExpression("^(Released|OnHold|Rejected)$")] string Disposition,
    [Required, StringLength(1000)] string Reason);
public record PackRequest(Guid RequestId, [Range(1,long.MaxValue)] long BatchId,
    [Range(typeof(decimal),"0.001","100")] decimal NetWeightKg,
    [Range(1,1000000)] int PackCount,
    [Range(typeof(decimal),"0","1000000")] decimal LossKg,
    DateOnly PackedOn, DateOnly BestBefore);
public record DispatchRequest([Range(1,long.MaxValue)] long CustomerId, [Required, StringLength(100)] string Reference,
    [Required, MinLength(1), MaxLength(50)] List<DispatchItemRequest> Items);
public record DispatchItemRequest([Range(1,long.MaxValue)] long LotId,[Range(1,1000000)] int Packs);
public record ReasonRequest([Required, StringLength(1000)] string Reason);

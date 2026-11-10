#nullable enable
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IStoreManagementService
{
    Task<bool> HasProductAsync(WinoAddOnProductType productType);
    Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType);
    Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId);
    Task<string?> GetCustomerPurchaseIdAsync(string serviceTicket, string publisherUserId);
}

using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundMicrosoftStoreService : IMicrosoftStoreService
{
    public bool HasAvailableUpdate => false;

    public Task<bool> HasProductAsync(WinoAddOnProductType productType)
        => Task.FromResult(false);

    public Task<StorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType)
        => Task.FromResult(StorePurchaseResult.NotPurchased);

    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId)
        => Task.FromResult<string?>(null);

    public Task PromptRatingDialogAsync()
        => Task.CompletedTask;

    public Task LaunchStorePageForReviewAsync()
        => Task.CompletedTask;

    public Task<bool> RefreshAvailabilityAsync()
        => Task.FromResult(false);

    public Task<bool> StartUpdateAsync()
        => Task.FromResult(false);
}

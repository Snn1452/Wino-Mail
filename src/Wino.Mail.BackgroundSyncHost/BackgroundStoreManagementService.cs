using System;
using System.Threading.Tasks;
using Windows.Services.Store;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using WinoStorePurchaseResult = Wino.Core.Domain.Enums.StorePurchaseResult;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundStoreManagementService : IStoreManagementService
{
    private const string UnlimitedAccountsOfferToken = "UnlimitedAccounts";
    private readonly StoreContext _storeContext;

    public BackgroundStoreManagementService()
    {
        _storeContext = StoreContext.GetDefault();
    }

    public async Task<bool> HasProductAsync(WinoAddOnProductType productType)
    {
        if (productType != WinoAddOnProductType.UNLIMITED_ACCOUNTS)
            return false;

        try
        {
            var license = await _storeContext.GetAppLicenseAsync();
            if (license is null)
                return false;

            foreach (var entry in license.AddOnLicenses)
            {
                var addOnLicense = entry.Value;
                if (addOnLicense.InAppOfferToken == UnlimitedAccountsOfferToken)
                    return addOnLicense.IsActive;
            }
        }
        catch
        {
            // Store services are optional for headless background synchronization.
        }

        return false;
    }

    public Task<WinoStorePurchaseResult> PurchaseAsync(WinoAddOnProductType productType)
        => Task.FromResult(WinoStorePurchaseResult.NotPurchased);

    public Task<string?> GetCustomerCollectionsIdAsync(string serviceTicket, string publisherUserId)
        => Task.FromResult<string?>(null);

    public Task<string?> GetCustomerPurchaseIdAsync(string serviceTicket, string publisherUserId)
        => Task.FromResult<string?>(null);
}

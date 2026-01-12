namespace SpuntiniBCGateway.Services.Spuntini
{
    public class SpuntiniItemsBCRequest
    {
        public static async Task<Dictionary<string, Dictionary<string, string>>> GetItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, string filter = "", string expand = "", EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("SPUNTINI", StringComparison.OrdinalIgnoreCase))
                ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'SPUNTINI'");

            return await ItemBCRequest.GetItemsAsync(client, config, company, filter, expand, logger, authHelper, cancellationToken);
        }
    }
}

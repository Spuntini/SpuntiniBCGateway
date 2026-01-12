namespace SpuntiniBCGateway.Services;

public static class UnitsOfMeasureBCRequest
{   
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetUnitsOfMeasureAsync(HttpClient client, IConfigurationRoot config, string? company = null, string keyDefinition = "code", string? filter = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(company);

        string url = config[$"Companies:{company}:UnitsOfMeasureData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:UnitsOfMeasureData:DestinationApiUrl required in config");

        if (!string.IsNullOrWhiteSpace(filter))
        {
            url += "&$filter=" + filter;
        }
        else
        {
            url += config[$"Companies:{company}:UnitsOfMeasureData:SelectAllFilter"] ?? "";
        }

        return await BcRequest.GetBcDataAsync(client, url, keyDefinition, EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
    }
}

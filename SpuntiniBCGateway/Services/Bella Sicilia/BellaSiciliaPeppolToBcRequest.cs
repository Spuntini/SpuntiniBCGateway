using System.Diagnostics;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Orchestrator for processing Peppol UBL documents for Bella Sicilia
/// Reads Peppol files from the configured source directory, determines document type,
/// and delegates to the appropriate handler (purchase invoice, purchase credit note, sales invoice, or sales credit note)
/// </summary>
public static class BellaSiciliaPeppolToBcRequest
{
    public static async Task<string> ProcessPeppolFilesAsync(HttpClient client, IConfigurationRoot config, string mode, string? company = null, List<string>? documentNumberList = null, Dictionary<string, Dictionary<string, string>>? allItemData = null,
    Dictionary<string, Dictionary<string, string>>? allCustomerData = null, Dictionary<string, string>? unitOfMeasuresDictionary = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        var stopwatch = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing Peppol files for company '{company}'.");

        // Clean up old files from success and error destination directories
        await CleanupOldFilesAsync(config, company, logger);

        string peppolSourcePath = config[$"Companies:{company}:PeppolData:Source"] ?? string.Empty;

        if (!Directory.Exists(peppolSourcePath))
        {
            if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Peppol source directory not found: {peppolSourcePath}");
            return "OK";
        }

        // Get all XML files from the source directory
        var peppolFiles = Directory.GetFiles(peppolSourcePath, "*.xml", SearchOption.TopDirectoryOnly);

        if (peppolFiles.Length == 0)
        {
            if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"No Peppol files found in {peppolSourcePath}");
            return "OK";
        }

        int processedCount = 0;
        int errorCount = 0;

        foreach (var peppolFile in peppolFiles)
        {
            try
            {
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Processing Peppol file: {Path.GetFileName(peppolFile)}");

                // Read and parse the Peppol document
                var peppolDocument = PeppolUblReader.ReadPeppolDocument(peppolFile);

                // Determine the document type and process accordingly
                var processedFile = await ProcessPeppolDocumentAsync(client, config, mode, company, peppolDocument, documentNumberList, allItemData, allCustomerData, unitOfMeasuresDictionary, logger, authHelper, cancellationToken);

                await MoveFileToDestinationAsync(peppolFile, config, company, processedFile, logger);
                processedCount++;
            }
            catch (Exception ex)
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);

                // Move file to error directory
                await MoveFileToDestinationAsync(peppolFile, config, company, false, logger);
                errorCount++;
            }
        }

        stopwatch.Stop();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Finished processing Peppol files for company '{company}' in {StringHelper.GetDurationString(stopwatch.Elapsed)}. Processed: {processedCount}, Errors: {errorCount}");

        return "OK";
    }

    private static async Task<bool> ProcessPeppolDocumentAsync(HttpClient client, IConfigurationRoot config, string mode, string company, PeppolDocument peppolDocument, List<string>? documentNumberList,  Dictionary<string, Dictionary<string, string>>? allItemData,
    Dictionary<string, Dictionary<string, string>>? allCustomerData, Dictionary<string, string>? unitOfMeasuresDictionary, EventLog? logger, AuthHelper? authHelper, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        if(documentNumberList != null && documentNumberList.Count > 0)
        {
            if (documentNumberList != null && documentNumberList.Count > 0)
                return false;
            
            peppolDocument.ForceUpdate = true;
        }          
     
        switch (peppolDocument.Header.DocumentType)
        {
            case PeppolDocumentType.VendorInvoice:
                // if (mode == "purchase" || mode == "allpurchase" || mode == "all")
                // {
                //     var response =await BellaSiciliaPurchaseInvoiceToBcRequest.ProcessPeppolInvoiceAsync(client, config, company, peppolDocument, logger, authHelper, cancellationToken);
                //     return (response != null && response.IsSuccessStatusCode);
                // }
                break;

            case PeppolDocumentType.VendorCreditNote:
                // if (mode == "purchase" || mode == "allpurchase" || mode == "all")
                // {
                //     var response =await BellaSiciliaPurchaseCreditNoteToBcRequest.ProcessPeppolCreditNoteAsync(client, config, company, peppolDocument, logger, authHelper, cancellationToken);
                //     return (response != null && response.IsSuccessStatusCode);
                // }
                break;

            case PeppolDocumentType.ClientInvoice:
                if (mode == "allsales" || mode == "sales" || mode == "all")
                {
                    var response = await BellaSiciliaSalesInvoiceToBcRequest.ProcessPeppolInvoiceAsync(client, config, company, peppolDocument, allItemData, allCustomerData, unitOfMeasuresDictionary, logger, authHelper, cancellationToken);
                    
                   return response != null && response.IsSuccessStatusCode;
                }
                break;

            case PeppolDocumentType.ClientCreditNote:
                if (mode == "allsales" || mode == "sales" || mode == "all")
                {
                    var response = await BellaSiciliaSalesCreditNoteToBcRequest.ProcessPeppolCreditNoteAsync(client, config, company, peppolDocument, allItemData, allCustomerData, unitOfMeasuresDictionary, logger, authHelper, cancellationToken);
                    return response != null && response.IsSuccessStatusCode;
                }

                break;

            default:
                throw new NotSupportedException($"Unsupported Peppol document type: {peppolDocument.Header.DocumentType}");
        }

        return false;
    }

    private static async Task MoveFileToDestinationAsync(string peppolFile, IConfigurationRoot config, string company, bool isSuccess, EventLog? logger)
    {
        try
        {
            string destinationKey = isSuccess ? "SuccessDestination" : "ErrorDestination";
            string destinationPath = config[$"Companies:{company}:PeppolData:{destinationKey}"] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(destinationPath))
                return;

            // Create destination directory if it doesn't exist
            if (!Directory.Exists(destinationPath))
                Directory.CreateDirectory(destinationPath);

            string fileName = Path.GetFileName(peppolFile);
            string destinationFile = Path.Combine(destinationPath, fileName);

            // If file already exists in destination, append timestamp
            if (File.Exists(destinationFile))
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                string extension = Path.GetExtension(fileName);
                destinationFile = Path.Combine(destinationPath, $"{nameWithoutExtension}_{timestamp}{extension}");
            }

            File.Move(peppolFile, destinationFile, true);
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Failed to move file {peppolFile}: {ex.Message}");
        }
    }

    private static async Task CleanupOldFilesAsync(IConfigurationRoot config, string company, EventLog? logger)
    {
        try
        {
            string keepFilesXDaysStr = config[$"Companies:{company}:PeppolData:KeepFilesXDays"] ?? "60";
            if (!int.TryParse(keepFilesXDaysStr, out int keepDays))
                keepDays = 60;

            DateTime cutoffDate = DateTime.Now.AddDays(-keepDays);

            // Clean up success destination
            string successDestination = config[$"Companies:{company}:PeppolData:SuccessDestination"] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(successDestination) && Directory.Exists(successDestination))
            {
                await CleanupDirectoryAsync(successDestination, cutoffDate, logger, company, "success");
            }

            // Clean up error destination
            string errorDestination = config[$"Companies:{company}:PeppolData:ErrorDestination"] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(errorDestination) && Directory.Exists(errorDestination))
            {
                await CleanupDirectoryAsync(errorDestination, cutoffDate, logger, company, "error");
            }
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Error cleaning up old Peppol files: {ex.Message}");
        }
    }

    private static async Task CleanupDirectoryAsync(string directoryPath, DateTime cutoffDate, EventLog? logger, string company, string destinationType)
    {
        try
        {
            var files = Directory.GetFiles(directoryPath, "*.xml", SearchOption.TopDirectoryOnly);
            int deletedCount = 0;

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffDate)
                {
                    File.Delete(file);
                    deletedCount++;
                }
            }

            if (deletedCount > 0 && logger != null)
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"Cleaned up {deletedCount} old files from {destinationType} destination directory.");
            }
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Error cleaning up {destinationType} destination directory: {ex.Message}");
        }
    }
}


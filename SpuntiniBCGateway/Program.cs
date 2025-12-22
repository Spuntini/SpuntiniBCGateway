
using SpuntiniBCGateway.Services;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Reflection;
using EventLog = SpuntiniBCGateway.Services.EventLog;

public class Program
{
    // Semaphore to prevent concurrent execution of RunGatewayAsync
    public static readonly SemaphoreSlim _gatewayExecutionLock = new SemaphoreSlim(1, 1);

    public static async Task Main(string[] args)
    {
        bool isService = !(Debugger.IsAttached || args.Contains("--console"));

        var builder = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                string env = hostingContext.HostingEnvironment.EnvironmentName;
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((hostContext, services) =>
            {
                var cfg = hostContext.Configuration;
                // Configure Azure AD JWT Bearer authentication
                string tenantId = cfg["Auth:TenantId"] ?? string.Empty;
                string clientId = cfg["Auth:ClientId"] ?? string.Empty;

                services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        if (!string.IsNullOrWhiteSpace(tenantId))
                        {
                            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                        }
                        // Audience should be the API (client) id or the App URI configured in AAD
                        if (!string.IsNullOrWhiteSpace(clientId))
                        {
                            options.Audience = clientId;
                        }
                        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                        {
                            ValidateIssuer = true
                        };
                    });

                services.AddAuthorization();
                // Swagger/OpenAPI
                services.AddEndpointsApiExplorer();
                services.AddSwaggerGen(c =>
                {
                    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SpuntiniBCGateway API", Version = "v1" });

                    // Include XML comments if available (requires GenerateDocumentationFile in csproj)
                    try
                    {
                        var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                        if (File.Exists(xmlPath))
                        {
                            c.IncludeXmlComments(xmlPath);
                        }
                    }
                    catch { }       
                });
                services.AddSingleton(sp => hostContext.Configuration);
                services.AddHostedService<Worker>();
            })
            .ConfigureWebHostDefaults(webHostBuilder =>
            {
                webHostBuilder.ConfigureKestrel(options => { });
                webHostBuilder.Configure((ctx, app) =>
                {
                    var appConfig = ctx.Configuration;
                    app.UseRouting();

                    app.UseAuthentication();
                    app.UseAuthorization();

                    // Enable middleware to serve generated Swagger as JSON endpoint and Swagger UI
                    app.UseSwagger();
                    app.UseSwaggerUI(c =>
                    {
                        c.SwaggerEndpoint("/swagger/v1/swagger.json", "SpuntiniBCGateway API v1");
                        c.RoutePrefix = "swagger"; // available at /swagger

                        // Configure Swagger UI OAuth client if Azure AD values are present
                        var swaggerClientId = appConfig["Auth:ClientId"] ?? string.Empty;
                        var swaggerTenant = appConfig["Auth:TenantId"] ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(swaggerClientId) && !string.IsNullOrWhiteSpace(swaggerTenant))
                        {
                            c.OAuthClientId(swaggerClientId);
                            c.OAuthUsePkce();
                            c.OAuthAppName("SpuntiniBCGateway Swagger UI");
                        }
                    });

                    app.UseEndpoints(endpoints =>
                    {
                        // Minimal API: RunGateway trigger via route parameters (documented in Swagger)
                        endpoints.MapPost("/api/run/{company}/{mode}", async (HttpContext context, string company, string mode) =>
                        {
                            try
                            {
                                // Attempt to acquire the lock with a timeout to prevent indefinite waiting
                                if (!await _gatewayExecutionLock.WaitAsync(TimeSpan.Zero, context.RequestAborted))
                                {
                                    context.Response.StatusCode = 429; // Too Many Requests
                                    await context.Response.WriteAsync("Gateway is already running. Please wait for the current execution to complete.");
                                    return;
                                }

                                try
                                {
                                    var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        ["company"] = company.ToUpperInvariant(),
                                        ["mode"] = mode
                                    };

                                    var childConfig = new ConfigurationBuilder()
                                        .AddConfiguration(appConfig)
                                        .AddInMemoryCollection(overrides.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)))
                                        .Build();

                                    var logger = new EventLog(childConfig);

                                    await RunGatewayAsync(childConfig, logger, context.RequestAborted).ConfigureAwait(false);

                                    context.Response.StatusCode = 200;
                                    await context.Response.WriteAsync("OK");
                                }
                                finally
                                {
                                    _gatewayExecutionLock.Release();
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                context.Response.StatusCode = 499;
                            }
                            catch (Exception ex)
                            {
                                context.Response.StatusCode = 500;
                                await context.Response.WriteAsync(ex.ToString());
                            }
                        }).RequireAuthorization().WithName("RunGateway");

                        endpoints.MapGet("/status", async context =>
                        {
                            context.Response.ContentType = "text/plain";
                            await context.Response.WriteAsync("OK");
                        });

                        endpoints.MapPost("/api/run", async context =>
                        {
                            try
                            {
                                // Attempt to acquire the lock with a timeout to prevent indefinite waiting
                                if (!await _gatewayExecutionLock.WaitAsync(TimeSpan.Zero, context.RequestAborted))
                                {
                                    context.Response.StatusCode = 429; // Too Many Requests
                                    await context.Response.WriteAsync("Gateway is already running. Please wait for the current execution to complete.");
                                    return;
                                }

                                try
                                {
                                    using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                                    var root = doc.RootElement;

                                    var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    string company = "";
                                    if (root.TryGetProperty("company", out var jCompany) && jCompany.ValueKind == JsonValueKind.String)
                                        company = jCompany.GetString()!.ToUpper();
                                    overrides["company"] = company;
                                    if (root.TryGetProperty("mode", out var jMode) && jMode.ValueKind == JsonValueKind.String)
                                        overrides["mode"] = jMode.GetString()!;
                                    
                                    // Build a child configuration which layers overrides on top of app config
                                    var childConfig = new ConfigurationBuilder()
                                        .AddConfiguration(appConfig)
                                        .AddInMemoryCollection(overrides.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)))
                                        .Build();

                                    var logger = new EventLog(childConfig);

                                    await RunGatewayAsync(childConfig, logger, context.RequestAborted).ConfigureAwait(false);

                                    context.Response.StatusCode = 200;
                                    await context.Response.WriteAsync("OK");
                                }
                                finally
                                {
                                    _gatewayExecutionLock.Release();
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                context.Response.StatusCode = 499; // client closed request
                            }
                            catch (Exception ex)
                            {
                                context.Response.StatusCode = 500;
                                byte[] bytes = Encoding.UTF8.GetBytes(ex.ToString());
                                await context.Response.Body.WriteAsync(bytes);
                            }
                        }).RequireAuthorization();
                    });
                });
            });

        if (isService)
        {
            builder.UseWindowsService();
        }
        else
        {
            builder.UseConsoleLifetime();
        }

        await builder.Build().RunAsync();
    }

    // Shared gateway logic for both console and service
    public static async Task RunGatewayAsync(IConfiguration config, EventLog logger, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string company = "";

        try
        {
            company = config["company"] ?? config["Company"] ?? string.Empty;

            await logger.InfoAsync(EventLog.GetMethodName(), company, "SpuntiniBCGateway process started.");

            string mode = config["mode"] ?? config["Mode"] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(company))
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway company not specified, no action performed.");
                return;
            }

            if (string.IsNullOrWhiteSpace(mode))
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway mode not specified, no action performed.");
                return;
            }

            string bcBaseUrl = config["BusinessCentral:BCBaseUrl"] ?? string.Empty;

            if (company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            {
                await RunBiebuyckMethod(config, mode, logger, cancellationToken);
            }
            else if (company.StartsWith("BELLA SICILIA", StringComparison.OrdinalIgnoreCase))
            {
                await RunBellaSiciliaMethod(config, mode, logger, cancellationToken);
            }
            else
            {
                await logger.WarningAsync(EventLog.GetMethodName(), company, $"Company '{company}' is not supported in this demo.");
            }
        }
        catch (Exception ex)
        {
            await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
        }
        finally
        {
            stopwatch.Stop();
            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway proces finished in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
        }
    }

    public static async Task RunBiebuyckMethod(IConfiguration config, string mode, EventLog logger, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string company = string.Empty;
        try
        {
            company = config["company"] ?? config["Company"] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(company))
                throw new ArgumentException("Company configuration is missing.");

            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} process started.");

            if (string.IsNullOrWhiteSpace(mode))
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} mode not specified, no action performed.");
                return;
            }
           
            string bcBaseUrl = config[$"Companies:{company}:BusinessCentral:BCBaseUrl"] ?? string.Empty;

            await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing company '{company}'.");

            Dictionary<string, Dictionary<string, string>>? allItemData = null;
            Dictionary<string, Dictionary<string, string>>? allCustomerData = null;
            Dictionary<string, Dictionary<string, string>>? allSupplierData = null;

            string fileDirectory = config["FileDirectory"] ?? string.Empty;

            var auth = new AuthHelper(config);
            using var httpClient = await auth.CreateHttpClientAsync();
            httpClient.BaseAddress = new Uri(bcBaseUrl);

            if (mode == "suppliers" || mode == "purchase" || mode == "allpurchase" || mode == "all")
            {
                allSupplierData = await BiebuyckSupplierCsvToBCRequest.GetSuppliersByCommentAsync(httpClient, (IConfigurationRoot)config, company, null, logger, auth, cancellationToken);
            }

            if (mode == "customers" || mode == "allsales" || mode == "sales" || mode == "all")
            {
                allCustomerData = await BiebuyckCustomerCsvToBCRequest.GetCustomersAsync(httpClient, (IConfigurationRoot)config, company, "", logger, auth, cancellationToken);
            }

            if (mode == "items" || mode == "purchase" || mode == "allpurchase" || mode == "allsales" || mode == "sales" || mode == "all")
            {
                allItemData = await BiebuyckItemsCsvToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "", logger, auth, cancellationToken);
            }
            else if (mode == "itemscogs")
            {
                allItemData = await BiebuyckItemsCsvToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "?$filter=startswith(no, 'A')", logger, auth, cancellationToken);
            }

            if (mode == "suppliers")
            {
                await BiebuyckSupplierCsvToBCRequest.ProcessSuppliersAsync(httpClient, (IConfigurationRoot)config, company, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "items")
            {
                await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
            }
            else if (mode == "customers")
            {
                await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "sales")
            {
                await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "allsales")
            {
                await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
                await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, allCustomerData, logger, auth, cancellationToken);
                await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "purchase")
            {
                await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "allpurchase")
            {
                await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
                await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "all")
            {
                await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
                await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, allCustomerData, logger, auth, cancellationToken);
                await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allSupplierData, logger, auth, cancellationToken);
                await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "itemscogs")
            {
                await DimensionBCRequest.ProcessItemCogsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
            }
            else
            {
                throw new NotSupportedException($"Mode '{mode}' is not supported for company '{company}'.");
            }
        }
        catch (Exception ex)
        {
            await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
        }
        finally
        {
            stopwatch.Stop();
            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} process finished in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
        }
    }

    public static async Task RunBellaSiciliaMethod(IConfiguration config, string mode, EventLog logger, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string company = string.Empty;

        try
        {
            company = config["company"] ?? config["Company"] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(company))
                throw new ArgumentException("Company configuration is missing.");

            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} process started.");

            if (string.IsNullOrWhiteSpace(mode))
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} mode not specified, no action performed.");
                return;
            }           

            string bcBaseUrl = config[$"Companies:{company}:BusinessCentral:BCBaseUrl"] ?? string.Empty;

            await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing company '{company}'.");

            Dictionary<string, Dictionary<string, string>>? allItemData = null;
            Dictionary<string, Dictionary<string, string>>? allCustomerData = null;
            Dictionary<string, Dictionary<string, string>>? allSupplierData = null;

            string fileDirectory = config["FileDirectory"] ?? string.Empty;

            var auth = new AuthHelper(config);
            using var httpClient = await auth.CreateHttpClientAsync();
            httpClient.BaseAddress = new Uri(bcBaseUrl);

            if (mode == "suppliers" || mode == "purchase" || mode == "allpurchase" || mode == "all")
            {
                // allSupplierData = await BellaSiciliaSupplierExcelToBCRequest.GetSuppliersByCommentAsync(httpClient, (IConfigurationRoot)config, company, null, logger, auth, cancellationToken);
            }

            if (mode == "customers" || mode == "allsales" || mode == "sales" || mode == "all")
            {
                // allCustomerData = await BellaSiciliaCustomerExcelToBCRequest.GetCustomersAsync(httpClient, (IConfigurationRoot)config, company, "", logger, auth, cancellationToken);
            }

            if (mode == "items" || mode == "purchase" || mode == "allpurchase" || mode == "allsales" || mode == "sales" || mode == "all")
            {
                allItemData = await BellaSiciliaItemsExcelToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "", logger, auth, cancellationToken);
            }
            else if (mode == "itemscogs")
            {
                allItemData = await BiebuyckItemsCsvToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "?$filter=startswith(no, 'A')", logger, auth, cancellationToken);
            }

            if (mode == "suppliers")
            {
                // await BellaSiciliaSupplierExcelToBCRequest.ProcessSuppliersAsync(httpClient, (IConfigurationRoot)config, company, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "items")
            {
                await BellaSiciliaItemsExcelToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
            }
            else if (mode == "customers")
            {
                // await BellaSiciliaCustomerExcelToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "sales")
            {
                // await BellaSiciliaSalesDeliveryNotesExcelToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "allsales")
            {
                await BellaSiciliaItemsExcelToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
                // await BellaSiciliaCustomerExcelToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, allCustomerData, logger, auth, cancellationToken);
                // await BellaSiciliaSalesDeliveryNotesExcelToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "purchase")
            {
                // await BellaSiciliaPurchaseReceiptsExcelToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "allpurchase")
            {
                await BellaSiciliaItemsExcelToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
                // await BellaSiciliaPurchaseReceiptsExcelToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "all")
            {
                await BellaSiciliaItemsExcelToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
                // await BellaSiciliaCustomerExcelToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, allCustomerData, logger, auth, cancellationToken);
                // await BellaSiciliaPurchaseReceiptsExcelToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allSupplierData, logger, auth, cancellationToken);
                // await BellaSiciliaSalesDeliveryNotesExcelToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, allItemData, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "itemscogs")
            {
                await DimensionBCRequest.ProcessItemCogsAsync(httpClient, (IConfigurationRoot)config, company, allItemData, logger, auth, cancellationToken);
            }
            else
            {
                throw new NotSupportedException($"Mode '{mode}' is not supported for company '{company}'.");
            }
        }
        catch (Exception ex)
        {
            await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
        }
        finally
        {
            stopwatch.Stop();
            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} proces finished in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
        }
    }
}

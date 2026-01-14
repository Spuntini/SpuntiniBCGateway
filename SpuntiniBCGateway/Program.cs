
using SpuntiniBCGateway.Services;
using SpuntiniBCGateway.Swagger;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Reflection;
using EventLog = SpuntiniBCGateway.Services.EventLog;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using SpuntiniBCGateway.Services.Spuntini;

public class Program
{
    // Semaphore to prevent concurrent execution of RunGatewayAsync
    public static readonly SemaphoreSlim _gatewayExecutionLock = new(1, 1);

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
                config.AddJsonFile("auth.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((hostContext, services) =>
            {
                var cfg = hostContext.Configuration;
                // Configure Azure AD JWT Bearer authentication
                string tenantId = cfg["Auth:TenantId"] ?? string.Empty;
                string clientId = cfg["Auth:ClientId"] ?? string.Empty;

                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        if (!string.IsNullOrWhiteSpace(tenantId))
                        {
                            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                        }
                        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                        {
                            ValidAudiences = [clientId, $"api://{clientId}"],
                            ValidateIssuer = true,
                            ValidateAudience = true
                        };
                        options.Events = new JwtBearerEvents
                        {
                            OnAuthenticationFailed = ctx =>
                            {
                                // log ctx.Exception to help diagnose
                                return Task.CompletedTask;
                            }
                        };

                    });

                services.AddAuthorization();
                // Swagger/OpenAPI
                services.AddEndpointsApiExplorer();
                services.AddSwaggerGen(c =>
                {
                    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SpuntiniBCGateway API", Version = "v1" });

                    // Add OAuth2 security definition for Azure AD
                    string tenantId = cfg["Auth:TenantId"] ?? string.Empty;
                    string clientId = cfg["Auth:ClientId"] ?? string.Empty;

                    // Use .default scope format for Azure AD
                    string apiScope = $"api://{clientId}/.default";
                    var scopes = new Dictionary<string, string> { { apiScope, "Access SpuntiniBCGateway API" } };

                    if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(clientId))
                    {
                        c.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
                        {
                            Type = SecuritySchemeType.OAuth2,
                            Flows = new OpenApiOAuthFlows
                            {
                                Implicit = new OpenApiOAuthFlow
                                {
                                    AuthorizationUrl = new Uri($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize"),
                                    TokenUrl = new Uri($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token"),
                                    Scopes = scopes
                                }
                            },
                            Description = "OAuth2 with Azure AD"
                        });

                        c.AddSecurityRequirement(new OpenApiSecurityRequirement
                        {
                            {
                                new OpenApiSecurityScheme
                                {
                                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "oauth2" }
                                },
                                scopes.Keys.ToArray()
                            }
                        });
                    }

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

                    // Add parameter enum filter for dropdown lists in Swagger UI
                    c.ParameterFilter<ParameterEnumFilter>();

                    // Add text parameter filter for unconstrained text parameters
                    c.ParameterFilter<TextParameterFilter>();
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

                        // Configure Swagger UI OAuth2 with Azure AD
                        var swaggerClientId = appConfig["SwaggerUI:SwaggerClientId"] ?? string.Empty;
                        var mainApiClientId = appConfig["Auth:ClientId"] ?? string.Empty;
                        var swaggerTenant = appConfig["Auth:TenantId"] ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(swaggerClientId) && !string.IsNullOrWhiteSpace(swaggerTenant))
                        {
                            c.OAuthClientId(swaggerClientId);
                            // Request the main API's .default scope
                            c.OAuthScopes($"api://{mainApiClientId}/.default");
                            c.OAuthUsePkce();
                            c.OAuthAppName("SpuntiniBCGateway Swagger UI");
                        }
                    });

                    app.UseEndpoints(endpoints =>
                    {
                        // <summary>
                        // Trigger gateway processing for a specific company and processing mode
                        // </summary>
                        // <param name="company">
                        // Company identifier. Valid values:
                        // - SPUNTINI PRODUCTION
                        // - SPUNTINI TEST
                        // - BIEBUYCK PRODUCTION
                        // - BIEBUYCK TEST
                        // - BELLA SICILIA PRODUCTION
                        // - BELLA SICILIA TEST
                        // </param>
                        // <param name="mode">
                        // Processing mode. Valid values:
                        // - items: Process items/products
                        // - customers: Process customers
                        // - suppliers: Process suppliers/vendors
                        // - sales: Process sales orders
                        // - purchase: Process purchase orders
                        // - itemscogs: Process item costs
                        // - all: Process all data types
                        // </param>
                        // <returns>200 OK if execution started successfully, 429 if gateway is already running, 500 on error</returns>
                        endpoints.MapPost("/api/run/{company}/{mode}", async (HttpContext context, string company, string mode, string? parameters = null) =>
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
                                    if (!string.IsNullOrWhiteSpace(parameters))
                                    {
                                        overrides["parameters"] = parameters;
                                    }

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

                        // <summary>
                        // Trigger gateway processing via JSON request body
                        // </summary>
                        // <remarks>
                        // Request body example:
                        // {
                        //   "company": "BIEBUYCK PRODUCTION",
                        //   "mode": "items"
                        // }
                        // 
                        // Valid company values: SPUNTINI PRODUCTION & TEST, BIEBUYCK PRODUCTION & TEST, BELLA SICILIA PRODUCTION & TEST
                        // Valid mode values: items, customers, suppliers, sales, purchase, itemscogs, all
                        // </remarks>
                        // <returns>200 OK if execution started successfully, 429 if gateway is already running, 500 on error</returns>
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
                                    if (root.TryGetProperty("parameters", out var jParameters) && jParameters.ValueKind == JsonValueKind.String)
                                        overrides["parameters"] = jParameters.GetString()!;

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
                        }).RequireAuthorization().WithName("RunGatewayJson");
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

            if (company.StartsWith("SPUNTINI", StringComparison.OrdinalIgnoreCase))
            {
                await RunSpuntiniMethod(config, mode, logger, cancellationToken);
            }
            else if (company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            {
                await RunBiebuyckMethod(config, mode, logger, cancellationToken);
            }
            else if (company.StartsWith("BELLA SICILIA", StringComparison.OrdinalIgnoreCase))
            {
                await RunBellaSiciliaMethod(config, mode, logger, cancellationToken);
            }
            else
            {
                throw new NotSupportedException($"Unsupported company {company}");
            }
        }
        catch (Exception ex)
        {
            await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway proces finished in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
        }
    }

    internal static List<string>? GetParametersList(IConfiguration config)
    {
        if (config == null) return null;

        var extraParameter = config["parameters"] ?? config["Parameters"] ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(extraParameter))
        {
            if (extraParameter.Contains(';'))
            {
                return [.. extraParameter.Split(';')];
            }
            else if (extraParameter.Contains(','))
            {
                return [.. extraParameter.Split(',')];
            }
            else if (extraParameter.Contains('|'))
            {
                return [.. extraParameter.Split('|')];
            }

            return [extraParameter];
        }

        return null;
    }

    public static async Task RunSpuntiniMethod(IConfiguration config, string mode, EventLog logger, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string company = string.Empty;
        try
        {
            company = config["company"] ?? config["Company"] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(company))
                throw new ArgumentException("Company configuration is missing.");

            if (string.IsNullOrWhiteSpace(mode))
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} mode not specified, no action performed.");
                return;
            }

            List<string>? parametersList = GetParametersList(config);

            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} - {mode} process started.");

            string bcBaseUrl = config[$"Companies:{company}:BusinessCentral:BCBaseUrl"] ?? string.Empty;

            Dictionary<string, Dictionary<string, string>>? allItemData = null;
            // Dictionary<string, Dictionary<string, string>>? allCustomerData = null;
            // Dictionary<string, Dictionary<string, string>>? allSupplierData = null;

            string fileDirectory = config["FileDirectory"] ?? string.Empty;

            var auth = new AuthHelper(config);
            using var httpClient = await auth.CreateHttpClientAsync();
            httpClient.BaseAddress = new Uri(bcBaseUrl);

            // if (mode == "suppliers" || mode == "purchase" || mode == "allpurchase" || mode == "all")
            // {
            //     allSupplierData = await BiebuyckSupplierCsvToBCRequest.GetSuppliersByCommentAsync(httpClient, (IConfigurationRoot)config, company, null, logger, auth, cancellationToken);
            // }

            // if (mode == "customers" || mode == "allsales" || mode == "sales" || mode == "all")
            // {
            //     allCustomerData = await BiebuyckCustomerCsvToBCRequest.GetCustomersAsync(httpClient, (IConfigurationRoot)config, company, "", logger, auth, cancellationToken);
            // }

            // if (mode == "items" || mode == "purchase" || mode == "allpurchase" || mode == "allsales" || mode == "sales" || mode == "all")
            // {
            //     allItemData = await BiebuyckItemsCsvToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "", "", logger, auth, cancellationToken);
            // }
            // else 
            if (mode == "itemscogs")
            {
                allItemData = await SpuntiniItemsBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "?$filter=startswith(no, 'A')", "$expand=defaultDimensions", logger, auth, cancellationToken);
            }

            // if (mode == "suppliers")
            // {
            //     await BiebuyckSupplierCsvToBCRequest.ProcessSuppliersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allSupplierData, logger, auth, cancellationToken);
            // }
            // else if (mode == "items")
            // {
            //     await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allItemData, logger, auth, cancellationToken);
            // }
            // else if (mode == "customers")
            // {
            //     await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allCustomerData, logger, auth, cancellationToken);
            // }
            // else if (mode == "sales")
            // {
            //     await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allItemData, allCustomerData, logger, auth, cancellationToken);
            // }
            // else if (mode == "allsales")
            // {
            //     await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
            //     await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, null, allCustomerData, logger, auth, cancellationToken);
            //     await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, allCustomerData, logger, auth, cancellationToken);
            // }
            // else if (mode == "purchase")
            // {
            //     await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allItemData, allSupplierData, logger, auth, cancellationToken);
            // }
            // else if (mode == "allpurchase")
            // {
            //     await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
            //     await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, allSupplierData, logger, auth, cancellationToken);
            // }
            // else if (mode == "all")
            // {
            //     await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
            //     await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, null, allCustomerData, logger, auth, cancellationToken);
            //     await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, allSupplierData, logger, auth, cancellationToken);
            //     await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, allCustomerData, logger, auth, cancellationToken);
            // }
            // else 
            if (mode == "itemscogs")
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
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} process finished in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
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

            if (string.IsNullOrWhiteSpace(mode))
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} mode not specified, no action performed.");
                return;
            }

            List<string>? parametersList = GetParametersList(config);

            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} - {mode} process started.");

            string bcBaseUrl = config[$"Companies:{company}:BusinessCentral:BCBaseUrl"] ?? string.Empty;

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
                allItemData = await BiebuyckItemsCsvToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "", "", logger, auth, cancellationToken);
            }
            else if (mode == "itemscogs")
            {
                allItemData = await BiebuyckItemsCsvToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "?$filter=startswith(no, 'A')", "$expand=defaultDimensions", logger, auth, cancellationToken);
            }

            if (mode == "suppliers")
            {
                await BiebuyckSupplierCsvToBCRequest.ProcessSuppliersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "items")
            {
                await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allItemData, logger, auth, cancellationToken);
            }
            else if (mode == "customers")
            {
                await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "sales")
            {
                await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allItemData, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "allsales")
            {
                await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
                await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, null, allCustomerData, logger, auth, cancellationToken);
                await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "purchase")
            {
                await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allItemData, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "allpurchase")
            {
                await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
                await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, allSupplierData, logger, auth, cancellationToken);
            }
            else if (mode == "all")
            {
                await BiebuyckItemsCsvToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
                await BiebuyckCustomerCsvToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, null, allCustomerData, logger, auth, cancellationToken);
                await BiebuyckPurchaseReceiptsCsvToBCRequest.ProcessPurchaseOrdersAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, allSupplierData, logger, auth, cancellationToken);
                await BiebuyckSalesDeliveryNotesCsvToBCRequest.ProcessSalesOrdersAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, allCustomerData, logger, auth, cancellationToken);
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
            throw;
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

            if (string.IsNullOrWhiteSpace(mode))
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} mode not specified, no action performed.");
                return;
            }

            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} - {mode} process started.");

            string bcBaseUrl = config[$"Companies:{company}:BusinessCentral:BCBaseUrl"] ?? string.Empty;

            List<string>? parametersList = GetParametersList(config);

            Dictionary<string, string>? unitsOfMeasureDictionary = null;
            Dictionary<string, Dictionary<string, string>>? allItemData = null;
            Dictionary<string, Dictionary<string, string>>? allCustomerData = null;
            Dictionary<string, Dictionary<string, string>>? allVatCustomerData = null;
            // Dictionary<string, Dictionary<string, string>>? allSupplierData = null;

            string fileDirectory = config["FileDirectory"] ?? string.Empty;

            var auth = new AuthHelper(config);
            using var httpClient = await auth.CreateHttpClientAsync();
            httpClient.BaseAddress = new Uri(bcBaseUrl);

            // if (mode == "suppliers" || mode == "purchase" || mode == "allpurchase" || mode == "all")
            // {
            //     // allSupplierData = await BellaSiciliaSupplierExcelToBCRequest.GetSuppliersByCommentAsync(httpClient, (IConfigurationRoot)config, company, null, logger, auth, cancellationToken);
            // }

            if (mode == "customers" || mode == "allsales" || mode == "sales" || mode == "all")
            {
                allCustomerData = await BellaSiciliaCustomersExcelToBCRequest.GetCustomersAsync(httpClient, (IConfigurationRoot)config, company, "no", "", logger, auth, cancellationToken);
            }

            if (mode == "items" || mode == "purchase" || mode == "allpurchase" || mode == "allsales" || mode == "sales" || mode == "all")
            {
                if (mode == "purchase" || mode == "allpurchase" || mode == "allsales" || mode == "sales" || mode == "all")
                {
                    unitsOfMeasureDictionary = [];

                    var uomsData = await UnitsOfMeasureBCRequest.GetUnitsOfMeasureAsync(httpClient, (IConfigurationRoot)config, company, "internationalStandardCode", "", logger, auth, cancellationToken);
                    foreach (var inCode in uomsData.Keys)
                    {
                        if (DictionaryHelper.TryGet(uomsData[inCode], "code", out var code) && code != null && !unitsOfMeasureDictionary.ContainsKey(inCode))
                            unitsOfMeasureDictionary.Add(inCode, code);
                    }
                }

                allItemData = await BellaSiciliaItemsExcelToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "", "", logger, auth, cancellationToken);
            }
            else if (mode == "itemscogs")
            {
                allItemData = await BellaSiciliaItemsExcelToBCRequest.GetItemsAsync(httpClient, (IConfigurationRoot)config, company, "?$filter=startswith(no, 'A')", "$expand=defaultDimensions", logger, auth, cancellationToken);
            }

            // if (mode == "suppliers")
            // {
            // await BellaSiciliaSupplierExcelToBCRequest.ProcessSuppliersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allSupplierData, logger, auth, cancellationToken);
            // }
            // else 
            if (mode == "items")
            {
                await BellaSiciliaItemsExcelToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allItemData, logger, auth, cancellationToken);
            }
            else if (mode == "customers")
            {
                await BellaSiciliaCustomersExcelToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, parametersList, allCustomerData, logger, auth, cancellationToken);
            }
            else if (mode == "sales")
            {
                allVatCustomerData = GetAllVatCustomers(allCustomerData);
                await BellaSiciliaPeppolToBcRequest.ProcessPeppolFilesAsync(httpClient, (IConfigurationRoot)config, mode, company, parametersList, allItemData, allVatCustomerData, unitsOfMeasureDictionary, logger, auth, cancellationToken);
            }
            else if (mode == "allsales")
            {
                await BellaSiciliaItemsExcelToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
                await BellaSiciliaCustomersExcelToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, null, allCustomerData, logger, auth, cancellationToken);
                allVatCustomerData = GetAllVatCustomers(allCustomerData);
                await BellaSiciliaPeppolToBcRequest.ProcessPeppolFilesAsync(httpClient, (IConfigurationRoot)config, mode, company, parametersList, allItemData, allVatCustomerData, unitsOfMeasureDictionary, logger, auth, cancellationToken);
            }
            //else if (mode == "purchase")
            //{
            // await BellaSiciliaPeppolToBcRequest.ProcessPeppolFilesAsync(httpClient, (IConfigurationRoot)config, mode, company, parametersList, allItemData, allVatCustomerData, unitsOfMeasureDictionary, logger, auth, cancellationToken);
            //}
            // else if (mode == "allpurchase")
            // {

            //     await BellaSiciliaItemsExcelToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
            //     await BellaSiciliaPeppolToBcRequest.ProcessPeppolFilesAsync(httpClient, (IConfigurationRoot)config, mode, company, parametersList, allItemData, allVatCustomerData, unitsOfMeasureDictionary, logger, auth, cancellationToken);
            // }
            else if (mode == "all")
            {
                await BellaSiciliaItemsExcelToBCRequest.ProcessItemsAsync(httpClient, (IConfigurationRoot)config, company, null, allItemData, logger, auth, cancellationToken);
                await BellaSiciliaCustomersExcelToBCRequest.ProcessCustomersAsync(httpClient, (IConfigurationRoot)config, company, null, allCustomerData, logger, auth, cancellationToken);
                allVatCustomerData = GetAllVatCustomers(allCustomerData);
                await BellaSiciliaPeppolToBcRequest.ProcessPeppolFilesAsync(httpClient, (IConfigurationRoot)config, mode, company, parametersList, allItemData, allVatCustomerData, unitsOfMeasureDictionary, logger, auth, cancellationToken);
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
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await logger.InfoAsync(EventLog.GetMethodName(), company, $"SpuntiniBCGateway {company} proces finished in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
        }
    }

    private static Dictionary<string, Dictionary<string, string>> GetAllVatCustomers(Dictionary<string, Dictionary<string, string>>? allCustomerData)
    {        
        var allVatCustomerData = new Dictionary<string, Dictionary<string, string>>();
        if (allCustomerData == null) return allVatCustomerData;

        foreach (var customer in allCustomerData.Values)
        {
            if (customer.TryGetValue("enterpriseNo", out var enterpriseNo) && !string.IsNullOrEmpty(enterpriseNo))
            {
                if (!allVatCustomerData.ContainsKey(enterpriseNo)) allVatCustomerData.Add(enterpriseNo, customer);
            }
            else if (customer.TryGetValue("vatRegistrationNo", out var vatRegistrationNo) && !string.IsNullOrEmpty(vatRegistrationNo))
            {
                if (!allVatCustomerData.ContainsKey(vatRegistrationNo)) allVatCustomerData.Add(vatRegistrationNo, customer);
            }
            else if (customer.TryGetValue("name", out var name) && !string.IsNullOrEmpty(name))
            {
                if (!allVatCustomerData.ContainsKey(name)) allVatCustomerData.Add(name, customer);
            }            
        }

        return allVatCustomerData;
    }
}

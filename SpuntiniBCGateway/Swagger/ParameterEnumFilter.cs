using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Any;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SpuntiniBCGateway.Swagger;

/// <summary>
/// Adds enum constraints to gateway API parameters for dropdown selection in Swagger UI
/// </summary>
public class ParameterEnumFilter : IParameterFilter
{
    public void Apply(OpenApiParameter parameter, ParameterFilterContext context)
    {
        // Add enum values for company parameter
        if (parameter.Name == "company" && parameter.In == ParameterLocation.Path)
        {
            parameter.Schema.Enum =
            [
                new OpenApiString(""),
                new OpenApiString("SPUNTINI PRODUCTION"),
                new OpenApiString("SPUNTINI TEST"),
                new OpenApiString("BIEBUYCK PRODUCTION"),
                new OpenApiString("BIEBUYCK TEST"),
                new OpenApiString("BELLA SICILIA PRODUCTION"),
                new OpenApiString("BELLA SICILIA TEST")
            ];
            parameter.Schema.Default = new OpenApiString("");
            parameter.Description = "Select the company to process. Valid values: SPUNTINI PRODUCTION & TEST, BIEBUYCK PRODUCTION & TEST, BELLA SICILIA PRODUCTION & TEST";
        }

        // Add enum values for mode parameter
        if (parameter.Name == "mode" && parameter.In == ParameterLocation.Path)
        {
            parameter.Schema.Enum =
            [
                new OpenApiString("items"),
                new OpenApiString("customers"),
                new OpenApiString("suppliers"),
                new OpenApiString("sales"),
                new OpenApiString("allsales"),
                new OpenApiString("purchase"),
                new OpenApiString("allpurchase"),
                new OpenApiString("itemscogs"),
                new OpenApiString("all")
            ];
            parameter.Schema.Default = new OpenApiString("items");
            parameter.Description = "Select the processing mode. Valid values: items, customers, suppliers, sales, purchase, itemscogs, all";
        }
    }
}

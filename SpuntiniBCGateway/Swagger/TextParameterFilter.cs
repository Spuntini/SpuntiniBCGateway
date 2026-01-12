using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SpuntiniBCGateway.Swagger;

/// <summary>
/// Configures text parameters without constraints for the Swagger UI
/// </summary>
public class TextParameterFilter : IParameterFilter
{
    public void Apply(OpenApiParameter parameter, ParameterFilterContext context)
    {
        // Configure text parameter without constraints
        if (parameter.Name == "text" && parameter.In == ParameterLocation.Query)
        {
            parameter.Schema.Type = "string";
            parameter.Schema.MinLength = null;
            parameter.Schema.MaxLength = null;
            parameter.Schema.Pattern = null;
            parameter.Schema.Enum = null;
            parameter.Description = "A text parameter without constraints";
        }

        // Configure parameters parameter without constraints
        if (parameter.Name == "parameters" && parameter.In == ParameterLocation.Query)
        {
            parameter.Schema.Type = "string";
            parameter.Schema.MinLength = null;
            parameter.Schema.MaxLength = null;
            parameter.Schema.Pattern = null;
            parameter.Schema.Enum = null;
            parameter.Description = "An parameters parameter without constraints";
        }
    }
}

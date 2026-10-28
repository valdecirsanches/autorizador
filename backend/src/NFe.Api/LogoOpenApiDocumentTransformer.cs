using Microsoft.OpenApi.Models;

namespace NFe.Api;

internal static class LogoOpenApiDocumentTransformer
{
    public static void AddLogoUploadSchema(OpenApiDocument document)
    {
        foreach (var (path, pathItem) in document.Paths)
        {
            if (!path.EndsWith("/logo", StringComparison.OrdinalIgnoreCase)
                || !pathItem.Operations.TryGetValue(OperationType.Post, out var operation))
            {
                continue;
            }

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Description = "Selecione o arquivo de imagem do logo.",
                Content =
                {
                    ["multipart/form-data"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Required = new HashSet<string>(StringComparer.Ordinal) { "arquivo" },
                            Properties =
                            {
                                ["arquivo"] = new OpenApiSchema
                                {
                                    Type = "string",
                                    Format = "binary",
                                    Description = "Imagem PNG, JPEG, WebP, GIF ou BMP (maximo 3 MB).",
                                },
                            },
                        },
                    },
                },
            };
        }
    }
}

using Microsoft.OpenApi.Models;
using NFe.Api;

namespace NFe.Tests;

public class LogoOpenApiDocumentTransformerTests
{
    [Fact]
    public void AddLogoUploadSchema_DeclaraCampoBinarioParaSeletorDeArquivo()
    {
        var document = new OpenApiDocument
        {
            Paths = new OpenApiPaths
            {
                ["/api/empresas/{empresa}/logo"] = PostPath(),
                ["/api/empresas/por-cnpj/{cnpj}/logo"] = PostPath(),
            },
        };

        LogoOpenApiDocumentTransformer.AddLogoUploadSchema(document);

        foreach (var path in document.Paths.Values)
        {
            var schema = path.Operations[OperationType.Post]
                .RequestBody!.Content["multipart/form-data"].Schema;

            Assert.Equal("object", schema.Type);
            Assert.Contains("arquivo", schema.Required);
            Assert.Equal("string", schema.Properties["arquivo"].Type);
            Assert.Equal("binary", schema.Properties["arquivo"].Format);
        }
    }

    private static OpenApiPathItem PostPath() => new()
    {
        Operations =
        {
            [OperationType.Post] = new OpenApiOperation
            {
                RequestBody = new OpenApiRequestBody
                {
                    Content =
                    {
                        ["multipart/form-data"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties = new Dictionary<string, OpenApiSchema>(),
                            },
                        },
                    },
                },
            },
        },
    };
}

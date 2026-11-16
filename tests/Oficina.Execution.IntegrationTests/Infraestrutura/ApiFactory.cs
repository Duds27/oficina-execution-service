using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Oficina.Execution.IntegrationTests.Infraestrutura;

/// <summary>
/// A API apontando para o DynamoDB Local da coleção, numa tabela só dela.
/// Em <c>Development</c>, o próprio serviço cria a tabela na inicialização.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string ChaveJwt = "OficinaMecanicaSecretKeyMustBe32CharactersMin!";

    private readonly string _serviceUrl;
    private readonly string _ambiente;

    public ApiFactory(string serviceUrl, string tabela, string ambiente = "Development")
    {
        _serviceUrl = serviceUrl;
        Tabela = tabela;
        _ambiente = ambiente;
    }

    public string Tabela { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_ambiente);
        builder.UseSetting("Jwt:Key", ChaveJwt);
        builder.UseSetting("DynamoDb:ServiceUrl", _serviceUrl);
        builder.UseSetting("DynamoDb:TableName", Tabela);
    }
}

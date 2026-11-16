using System.Net.Http.Headers;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Oficina.Execution.IntegrationTests.Infraestrutura;

/// <summary>Tokens no formato dos emissores reais (os-service e Lambda), com a chave de teste.</summary>
public static class Tokens
{
    /// <summary>Login do atendente/mecânico no os-service: claims curtas <c>unique_name</c> e <c>role</c>.</summary>
    public static string DaOficina(string nome = "joao", string papel = "Admin") =>
        Criar(new Dictionary<string, object> { ["unique_name"] = nome, ["role"] = papel });

    /// <summary>Login por CPF na Lambda: papel <c>Cliente</c>.</summary>
    public static string DeCliente() =>
        Criar(new Dictionary<string, object>
        {
            ["name"] = "Maria",
            ["role"] = "Cliente",
            ["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] = "Cliente",
            ["clienteId"] = 7
        });

    public static HttpClient Autenticado(this HttpClient client, string token)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Criar(Dictionary<string, object> claims) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "OficinaMecanicaBackend",
        Audience = "OficinaMecanicaBackend",
        Expires = DateTime.UtcNow.AddHours(1),
        Claims = claims,
        SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.ASCII.GetBytes(ApiFactory.ChaveJwt)), SecurityAlgorithms.HmacSha256)
    });
}

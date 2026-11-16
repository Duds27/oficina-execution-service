using System.Security.Claims;

namespace Oficina.Execution.API.Security;

/// <summary>Políticas de autorização da API.</summary>
public static class Politicas
{
    /// <summary>
    /// Equipe da oficina. O token de cliente (papel <c>Cliente</c>, emitido pela Lambda no login
    /// por CPF) é válido, mas não opera a fila de execução.
    /// </summary>
    public const string Oficina = "Oficina";

    public static readonly string[] PapeisDaOficina = ["Admin", "Mecanico"];

    /// <summary>Quem fez a operação, para a linha do tempo e o evento <c>DiagnosticoIniciado</c>.</summary>
    public static string Autor(ClaimsPrincipal usuario)
    {
        ArgumentNullException.ThrowIfNull(usuario);

        var nome = usuario.Identity?.Name
            ?? usuario.FindFirst("name")?.Value
            ?? usuario.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return string.IsNullOrWhiteSpace(nome) ? "desconhecido" : nome;
    }
}

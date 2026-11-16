namespace Oficina.Execution.Domain;

/// <summary>Tipos de registro na linha do tempo da execução.</summary>
public enum TipoApontamento
{
    Abertura,
    DiagnosticoIniciado,
    DiagnosticoConcluido,
    ExecucaoIniciada,
    ExecucaoRejeitada,
    ReparoIniciado,
    Observacao,
    Finalizada,
    Cancelada
}

/// <summary>
/// Um registro da linha do tempo: cada mudança de etapa gera um, e o mecânico pode
/// registrar observações livres. <see cref="Id"/> só garante unicidade; a ordem vem de <see cref="Em"/>.
/// </summary>
public sealed record Apontamento(string Id, TipoApontamento Tipo, string Texto, string Autor, DateTime Em)
{
    public static Apontamento Novo(TipoApontamento tipo, string texto, string autor, DateTime em) =>
        new(Guid.NewGuid().ToString("N"), tipo, texto, autor, em);
}

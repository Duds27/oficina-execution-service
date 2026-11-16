using Oficina.Execution.Domain;

namespace Oficina.Execution.Application.DTOs;

public sealed record VeiculoDto(string Placa, string Marca, string Modelo, int Ano);

public sealed record ServicoDto(string Descricao, decimal Quantidade);

public sealed record ApontamentoDto(string Tipo, string Texto, string Autor, DateTime Em);

/// <summary>Linha da fila de trabalho de uma etapa.</summary>
public sealed record ItemFilaDto(
    int OsId,
    string NumeroOS,
    string Etapa,
    DateTime EtapaDesde,
    VeiculoDto? Veiculo,
    string? Descricao,
    string? Mecanico);

/// <summary>Execução com a linha do tempo completa.</summary>
public sealed record ExecucaoDto(
    int OsId,
    Guid CorrelationId,
    string NumeroOS,
    string Etapa,
    DateTime EtapaDesde,
    VeiculoDto? Veiculo,
    string? Descricao,
    string? Mecanico,
    IReadOnlyList<ServicoDto> Servicos,
    string? ObservacoesDiagnostico,
    string? MotivoCancelamento,
    DateTime CriadaEm,
    DateTime? DiagnosticoIniciadoEm,
    DateTime? DiagnosticoConcluidoEm,
    DateTime? ExecucaoIniciadaEm,
    DateTime? ReparoIniciadoEm,
    DateTime? FinalizadaEm,
    DateTime? CanceladaEm,
    int Versao,
    IReadOnlyList<ApontamentoDto> Apontamentos);

public static class MapeamentoDtos
{
    public static ExecucaoDto ParaDto(this Execucao execucao)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        var e = execucao.Estado;
        return new ExecucaoDto(
            e.OsId,
            e.CorrelationId,
            e.NumeroOS,
            e.Etapa.ToString(),
            e.EtapaDesde,
            ParaDto(e.Veiculo),
            e.Descricao,
            e.Mecanico,
            e.Servicos.Select(s => new ServicoDto(s.Descricao, s.Quantidade)).ToList(),
            e.ObservacoesDiagnostico,
            e.MotivoCancelamento,
            e.CriadaEm,
            e.DiagnosticoIniciadoEm,
            e.DiagnosticoConcluidoEm,
            e.ExecucaoIniciadaEm,
            e.ReparoIniciadoEm,
            e.FinalizadaEm,
            e.CanceladaEm,
            e.Version,
            execucao.Apontamentos.Select(ParaDto).ToList());
    }

    public static ItemFilaDto ParaItemFila(this EstadoExecucao e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new ItemFilaDto(e.OsId, e.NumeroOS, e.Etapa.ToString(), e.EtapaDesde, ParaDto(e.Veiculo), e.Descricao, e.Mecanico);
    }

    public static ApontamentoDto ParaDto(this Apontamento a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new ApontamentoDto(a.Tipo.ToString(), a.Texto, a.Autor, a.Em);
    }

    private static VeiculoDto? ParaDto(Veiculo? v) => v is null ? null : new VeiculoDto(v.Placa, v.Marca, v.Modelo, v.Ano);
}

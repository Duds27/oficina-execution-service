using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Application.DTOs;
using Oficina.Execution.Application.Mensagens;
using Oficina.Execution.Domain;

namespace Oficina.Execution.Application.UseCases;

/// <summary>
/// Operações do mecânico (API). As que a saga precisa saber publicam o evento pelo outbox,
/// na mesma gravação da mudança: <c>DiagnosticoIniciado</c>, <c>DiagnosticoConcluido</c>
/// e <c>ExecucaoFinalizada</c>.
/// </summary>
public sealed class ExecucaoUseCases
{
    /// <summary>
    /// Um consumidor pode gravar a mesma execução entre a leitura e a gravação da API.
    /// Nesse caso, relê e aplica de novo, até este limite.
    /// </summary>
    private const int TentativasEmConflito = 3;

    private readonly IExecucaoRepositorio _repositorio;
    private readonly TimeProvider _relogio;

    public ExecucaoUseCases(IExecucaoRepositorio repositorio, TimeProvider relogio)
    {
        _repositorio = repositorio;
        _relogio = relogio;
    }

    public async Task<ExecucaoDto?> ObterAsync(int osId, CancellationToken cancellationToken = default) =>
        (await _repositorio.ObterAsync(osId, cancellationToken))?.ParaDto();

    public async Task<IReadOnlyList<ItemFilaDto>> ListarFilaAsync(EtapaExecucao etapa, int limite, CancellationToken cancellationToken = default) =>
        (await _repositorio.ListarFilaAsync(etapa, limite, cancellationToken)).Select(e => e.ParaItemFila()).ToList();

    public Task<Resultado<ExecucaoDto>> IniciarDiagnosticoAsync(int osId, string mecanico, CancellationToken cancellationToken = default) =>
        AlterarAsync(osId, (execucao, agora) =>
        {
            execucao.IniciarDiagnostico(mecanico, agora);
            return [FabricaMensagens.DiagnosticoIniciado(execucao, agora)];
        }, cancellationToken);

    public Task<Resultado<ExecucaoDto>> ConcluirDiagnosticoAsync(int osId, string? observacoes, string autor, CancellationToken cancellationToken = default) =>
        AlterarAsync(osId, (execucao, agora) =>
        {
            execucao.ConcluirDiagnostico(observacoes, autor, agora);
            return [FabricaMensagens.DiagnosticoConcluido(execucao, agora)];
        }, cancellationToken);

    /// <summary>Só a linha do tempo: a saga não tem um evento para o início do reparo.</summary>
    public Task<Resultado<ExecucaoDto>> IniciarReparoAsync(int osId, string autor, CancellationToken cancellationToken = default) =>
        AlterarAsync(osId, (execucao, agora) =>
        {
            execucao.IniciarReparo(autor, agora);
            return [];
        }, cancellationToken);

    public async Task<Resultado<ApontamentoDto>> RegistrarApontamentoAsync(int osId, string texto, string autor, CancellationToken cancellationToken = default)
    {
        var resultado = await AlterarAsync(osId, (execucao, agora) =>
        {
            execucao.RegistrarObservacao(texto, autor, agora);
            return [];
        }, cancellationToken);

        return resultado.Status == StatusResultado.Ok
            ? Resultado<ApontamentoDto>.Ok(resultado.Valor!.Apontamentos[^1])
            : new Resultado<ApontamentoDto>(resultado.Status, null, resultado.Erro);
    }

    public async Task<Resultado<ExecucaoDto>> FinalizarAsync(int osId, string autor, CancellationToken cancellationToken = default)
    {
        var duracao = 0;
        var resultado = await AlterarAsync(osId, (execucao, agora) =>
        {
            duracao = execucao.Finalizar(autor, agora);
            return [FabricaMensagens.ExecucaoFinalizada(execucao, duracao, agora)];
        }, cancellationToken);

        // Só depois de gravar: a operação pode rodar de novo num conflito de concorrência.
        if (resultado.Status == StatusResultado.Ok)
            MetricasExecucao.Duracao.Record(duracao);
        return resultado;
    }

    private async Task<Resultado<ExecucaoDto>> AlterarAsync(
        int osId,
        Func<Execucao, DateTime, IReadOnlyList<object>> operacao,
        CancellationToken cancellationToken)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            var execucao = await _repositorio.ObterAsync(osId, cancellationToken);
            if (execucao is null)
                return Resultado<ExecucaoDto>.NaoEncontrado($"Não há execução para a OS {osId}.");

            IReadOnlyList<object> mensagens;
            try
            {
                mensagens = operacao(execucao, _relogio.GetUtcNow().UtcDateTime);
            }
            catch (EtapaInvalidaException ex)
            {
                return Resultado<ExecucaoDto>.Conflito(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Resultado<ExecucaoDto>.Invalido(ex.Message);
            }

            try
            {
                await _repositorio.SalvarAsync(execucao, mensagens, null, cancellationToken);
                return Resultado<ExecucaoDto>.Ok(execucao.ParaDto());
            }
            catch (ConflitoDeVersaoException) when (tentativa < TentativasEmConflito)
            {
                // Relê e tenta de novo.
            }
            catch (ConflitoDeVersaoException)
            {
                return Resultado<ExecucaoDto>.Conflito($"A execução da OS {osId} está sendo alterada ao mesmo tempo. Tente de novo.");
            }
        }
    }
}

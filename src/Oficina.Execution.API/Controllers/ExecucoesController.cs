using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oficina.Execution.API.Security;
using Oficina.Execution.Application.DTOs;
using Oficina.Execution.Application.UseCases;
using Oficina.Execution.Domain;

namespace Oficina.Execution.API.Controllers;

/// <summary>Corpo de <c>POST diagnostico/concluir</c>.</summary>
public sealed record ConcluirDiagnosticoRequest([MaxLength(2000)] string? Observacoes);

/// <summary>Corpo de <c>POST apontamentos</c>.</summary>
public sealed record ApontamentoRequest([Required, MaxLength(2000)] string Texto);

/// <summary>
/// Fila de execução da oficina: diagnóstico e reparo de cada OS.
/// O prefixo <c>/api/execucoes</c> é exclusivo deste serviço (roteamento por caminho no API Gateway).
/// </summary>
[ApiController]
[Route("api/execucoes")]
[Authorize(Policy = Politicas.Oficina)]
[Produces("application/json")]
public sealed class ExecucoesController : ControllerBase
{
    private readonly ExecucaoUseCases _useCases;

    public ExecucoesController(ExecucaoUseCases useCases) => _useCases = useCases;

    /// <summary>Fila de uma etapa, por ordem de chegada nela.</summary>
    /// <param name="etapa">Etapa (padrão: <c>DiagnosticoPendente</c>).</param>
    /// <param name="limite">Quantos itens, de 1 a 100 (padrão: 50).</param>
    /// <param name="cancellationToken">Cancelamento da requisição.</param>
    [HttpGet("fila")]
    [ProducesResponseType(typeof(IReadOnlyList<ItemFilaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ItemFilaDto>>> Fila(
        [FromQuery] EtapaExecucao etapa = EtapaExecucao.DiagnosticoPendente,
        [FromQuery, Range(1, 100)] int limite = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await _useCases.ListarFilaAsync(etapa, limite, cancellationToken));

    /// <summary>Execução da OS com a linha do tempo de apontamentos.</summary>
    [HttpGet("{osId:int}")]
    [ProducesResponseType(typeof(ExecucaoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExecucaoDto>> Obter(int osId, CancellationToken cancellationToken)
    {
        var execucao = await _useCases.ObterAsync(osId, cancellationToken);
        return execucao is null ? NotFound(new { error = $"Não há execução para a OS {osId}." }) : Ok(execucao);
    }

    /// <summary>O mecânico (usuário do token) inicia o diagnóstico. Publica <c>DiagnosticoIniciado</c>.</summary>
    [HttpPost("{osId:int}/diagnostico/iniciar")]
    [ProducesResponseType(typeof(ExecucaoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExecucaoDto>> IniciarDiagnostico(int osId, CancellationToken cancellationToken) =>
        Responder(await _useCases.IniciarDiagnosticoAsync(osId, Politicas.Autor(User), cancellationToken));

    /// <summary>Conclui o diagnóstico em andamento, com observações. Publica <c>DiagnosticoConcluido</c>.</summary>
    [HttpPost("{osId:int}/diagnostico/concluir")]
    [ProducesResponseType(typeof(ExecucaoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExecucaoDto>> ConcluirDiagnostico(
        int osId, [FromBody] ConcluirDiagnosticoRequest? corpo, CancellationToken cancellationToken) =>
        Responder(await _useCases.ConcluirDiagnosticoAsync(osId, corpo?.Observacoes, Politicas.Autor(User), cancellationToken));

    /// <summary>Marca o início do reparo, depois de a saga liberar a execução (pagamento confirmado).</summary>
    [HttpPost("{osId:int}/reparo/iniciar")]
    [ProducesResponseType(typeof(ExecucaoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExecucaoDto>> IniciarReparo(int osId, CancellationToken cancellationToken) =>
        Responder(await _useCases.IniciarReparoAsync(osId, Politicas.Autor(User), cancellationToken));

    /// <summary>Registra uma observação livre na linha do tempo.</summary>
    [HttpPost("{osId:int}/apontamentos")]
    [ProducesResponseType(typeof(ApontamentoDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApontamentoDto>> RegistrarApontamento(
        int osId, [FromBody] ApontamentoRequest corpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(corpo);
        var resultado = await _useCases.RegistrarApontamentoAsync(osId, corpo.Texto, Politicas.Autor(User), cancellationToken);
        return resultado.Status == StatusResultado.Ok
            ? CreatedAtAction(nameof(Obter), new { osId }, resultado.Valor)
            : Erro(resultado.Status, resultado.Erro);
    }

    /// <summary>Finaliza o reparo em andamento. Publica <c>ExecucaoFinalizada</c> com a duração.</summary>
    [HttpPost("{osId:int}/finalizar")]
    [ProducesResponseType(typeof(ExecucaoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExecucaoDto>> Finalizar(int osId, CancellationToken cancellationToken) =>
        Responder(await _useCases.FinalizarAsync(osId, Politicas.Autor(User), cancellationToken));

    private ActionResult<ExecucaoDto> Responder(Resultado<ExecucaoDto> resultado) =>
        resultado.Status == StatusResultado.Ok ? Ok(resultado.Valor) : Erro(resultado.Status, resultado.Erro);

    private ObjectResult Erro(StatusResultado status, string? erro) => StatusCode(status switch
    {
        StatusResultado.NaoEncontrado => StatusCodes.Status404NotFound,
        StatusResultado.Conflito => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    }, new { error = erro });
}

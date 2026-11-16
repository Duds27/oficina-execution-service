namespace Oficina.Execution.Domain;

/// <summary>Veículo da OS, como veio no <c>SolicitarDiagnostico</c>.</summary>
public sealed record Veiculo(string Placa, string Marca, string Modelo, int Ano);

/// <summary>Serviço aprovado no orçamento, como veio no <c>IniciarExecucao</c>.</summary>
public sealed record ServicoPrevisto(string Descricao, decimal Quantidade);

/// <summary>Resposta da execução ao pedido de início: aceito, ou rejeitado com o motivo.</summary>
public sealed record ResultadoInicioExecucao(bool Aceita, string? Motivo)
{
    public static ResultadoInicioExecucao Aceitar() => new(true, null);

    public static ResultadoInicioExecucao Rejeitar(string motivo) => new(false, motivo);
}

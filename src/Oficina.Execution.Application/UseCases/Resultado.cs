namespace Oficina.Execution.Application.UseCases;

public enum StatusResultado
{
    Ok,
    Invalido,
    NaoEncontrado,
    Conflito
}

/// <summary>Resultado de uma operação da API: o valor, ou o motivo de não ter dado certo.</summary>
public sealed record Resultado<T>(StatusResultado Status, T? Valor, string? Erro)
{
    public static Resultado<T> Ok(T valor) => new(StatusResultado.Ok, valor, null);

    public static Resultado<T> Invalido(string erro) => new(StatusResultado.Invalido, default, erro);

    public static Resultado<T> NaoEncontrado(string erro) => new(StatusResultado.NaoEncontrado, default, erro);

    public static Resultado<T> Conflito(string erro) => new(StatusResultado.Conflito, default, erro);
}

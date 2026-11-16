namespace Oficina.Execution.IntegrationTests.Infraestrutura;

/// <summary>
/// Polling com prazo. Usado no lugar do <c>Published.Any</c> do test harness, que deixa de
/// esperar mensagens novas depois do primeiro período de inatividade.
/// </summary>
public static class Esperar
{
    public static async Task AteAsync(Func<bool> condicao, string erro, int segundos = 15)
    {
        ArgumentNullException.ThrowIfNull(condicao);
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (!condicao())
        {
            Assert.True(DateTime.UtcNow < limite, erro);
            await Task.Delay(50);
        }
    }

    public static async Task<T> AteAsync<T>(Func<Task<T?>> obter, Func<T, bool> condicao, string erro, int segundos = 15)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(obter);
        ArgumentNullException.ThrowIfNull(condicao);
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (true)
        {
            var valor = await obter();
            if (valor is not null && condicao(valor))
                return valor;
            Assert.True(DateTime.UtcNow < limite, erro);
            await Task.Delay(50);
        }
    }
}

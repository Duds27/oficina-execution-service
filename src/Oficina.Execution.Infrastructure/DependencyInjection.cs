using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Oficina.Execution.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registra persistência, mensageria e integrações externas.
    /// Cada dependência adiciona aqui o seu health check com a tag "ready",
    /// que passa a compor o <c>/health/ready</c>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}

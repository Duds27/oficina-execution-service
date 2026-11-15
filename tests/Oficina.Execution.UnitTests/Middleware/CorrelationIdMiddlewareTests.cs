using Microsoft.AspNetCore.Http;
using Oficina.Execution.API.Middleware;

namespace Oficina.Execution.UnitTests.Middleware;

public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task Usa_o_id_recebido_no_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "abc-123";
        string? idNoPipeline = null;
        var middleware = new CorrelationIdMiddleware(ctx =>
        {
            idNoPipeline = ctx.TraceIdentifier;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal("abc-123", idNoPipeline);
    }

    [Fact]
    public async Task Gera_um_guid_quando_o_header_nao_vem()
    {
        var context = new DefaultHttpContext();
        string? idNoPipeline = null;
        var middleware = new CorrelationIdMiddleware(ctx =>
        {
            idNoPipeline = ctx.TraceIdentifier;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(Guid.TryParse(idNoPipeline, out _));
    }
}
